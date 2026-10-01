using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.SqlServer.Management.UI.ConnectionDlg;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;

namespace SsmsAutoConnect
{
    /// <summary>
    /// The ONLY place that touches non-public SSMS 18 internals. Every internal member is optional:
    /// if one is missing (different SSMS build), we fall back to the public IObjectExplorerService API.
    ///
    /// Runtime type behind IObjectExplorerService (SSMS 18.10):
    ///   Microsoft.SqlServer.Management.SqlStudio.Explorer.ObjectExplorerService
    ///   (Common7\IDE\Extensions\Application\Microsoft.SqlServer.Management.SqlStudio.Explorer.dll)
    /// Internal members used (see CLAUDE.md):
    ///   static IDbConnection ValidateConnection(UIConnectionInfo, IServerType)   [internal static]
    ///   void ConnectToServer(UIConnectionInfo, IDbConnection, bool validate)     [private]
    ///   ObjectExplorerControl Tree { get; }                                      [internal]
    ///   ObjectExplorerControl.Hierarchies : Dictionary&lt;string, IExplorerHierarchy&gt; [internal]
    /// </summary>
    internal sealed class ObjectExplorerBridge
    {
        private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags StaticNonPublic = BindingFlags.Static | BindingFlags.NonPublic;

        private readonly IObjectExplorerService oe;
        private readonly MethodInfo validateConnection;
        private readonly MethodInfo connectWithLiveConnection;
        private readonly PropertyInfo treeProperty;

        public ObjectExplorerBridge(IObjectExplorerService oe)
        {
            this.oe = oe ?? throw new ArgumentNullException(nameof(oe));
            Type t = oe.GetType();

            validateConnection = t.GetMethods(StaticNonPublic | BindingFlags.Public).FirstOrDefault(m =>
                m.Name == "ValidateConnection" &&
                ParamTypes(m).Length == 2 &&
                ParamTypes(m)[0] == typeof(UIConnectionInfo) &&
                typeof(IDbConnection).IsAssignableFrom(m.ReturnType));

            connectWithLiveConnection = t.GetMethods(InstanceNonPublic).FirstOrDefault(m =>
                m.Name == "ConnectToServer" &&
                ParamTypes(m).SequenceEqual(new[] { typeof(UIConnectionInfo), typeof(IDbConnection), typeof(bool) }));

            treeProperty = t.GetProperty("Tree", InstanceNonPublic);

            UsesInternalConnect = validateConnection != null && connectWithLiveConnection != null;
            Log.Info($"OE service type {t.FullName}; internal ValidateConnection={(validateConnection != null)}, " +
                     $"ConnectToServer(ci,conn,bool)={(connectWithLiveConnection != null)}, Tree={(treeProperty != null)}");
        }

        /// <summary>True when the off-UI-thread validate + attach-live-connection route is available.</summary>
        public bool UsesInternalConnect { get; }

        /// <summary>
        /// Opens and validates the connection. Safe to call on a background thread: it does the slow,
        /// network-bound part that SSMS would otherwise do on the UI thread. Throws on failure.
        /// Returns the open connection for <see cref="AttachToObjectExplorer"/> (null in fallback mode).
        /// </summary>
        public IDbConnection Validate(UIConnectionInfo ci)
        {
            if (UsesInternalConnect)
            {
                IServerType serverType = UIConnectionInfoUtil.GetServerType(ci);
                try
                {
                    return (IDbConnection)validateConnection.Invoke(null, new object[] { ci, serverType });
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    throw tie.InnerException;
                }
            }

            // Fallback: prove reachability ourselves so the public ConnectToServer (which connects
            // on the UI thread and pops a message box on failure) is only called for good servers.
            var csb = new SqlConnectionStringBuilder
            {
                DataSource = ci.ServerName,
                InitialCatalog = ci.AdvancedOptions["DATABASE"] ?? string.Empty,
                IntegratedSecurity = ci.AuthenticationType == 0,
                ConnectTimeout = 15,
                ApplicationName = "SsmsAutoConnect",
            };
            if (ci.AuthenticationType != 0)
            {
                csb.UserID = ci.UserName;
                csb.Password = ci.Password;
            }
            using (var conn = new SqlConnection(csb.ConnectionString))
            {
                conn.Open();
            }
            return null;
        }

        /// <summary>Adds the server to Object Explorer. UI thread only.</summary>
        public void AttachToObjectExplorer(UIConnectionInfo ci, IDbConnection liveConnection)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (UsesInternalConnect && liveConnection != null)
            {
                try
                {
                    // Same call SSMS makes after its own Connect dialog validated the connection.
                    connectWithLiveConnection.Invoke(oe, new object[] { ci, liveConnection, false });
                    return;
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    throw tie.InnerException;
                }
            }
            oe.ConnectToServer(ci);
        }

        /// <summary>
        /// Server names currently connected in Object Explorer (as typed when connecting).
        /// Returns null if the internal tree isn't accessible. UI thread only.
        /// </summary>
        public HashSet<string> GetConnectedServerNames()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            object tree = GetTree();
            if (tree == null)
                return null;

            PropertyInfo hierarchiesProperty = tree.GetType().GetProperty("Hierarchies", InstanceNonPublic | BindingFlags.Public);
            if (!(hierarchiesProperty?.GetValue(tree) is IDictionary hierarchies))
                return null;

            // Keys come from SharedConnectionUtil.GetConnectionKeyName: "<ServerName> (SQLServer, trusted)".
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object key in hierarchies.Keys)
            {
                string s = key as string;
                if (string.IsNullOrEmpty(s))
                    continue;
                int paren = s.IndexOf(" (", StringComparison.Ordinal);
                names.Add(paren > 0 ? s.Substring(0, paren) : s);
            }
            return names;
        }

        /// <summary>
        /// The server root node just added by <see cref="AttachToObjectExplorer"/> (AddHierarchy selects it).
        /// UI thread only. Null if it can't be determined.
        /// </summary>
        public HierarchyTreeNode GetSelectedServerRoot()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return (GetTree() as TreeView)?.SelectedNode is HierarchyTreeNode root && root.Parent == null ? root : null;
        }

        /// <summary>
        /// Expands Databases under <paramref name="root"/> and selects the database node, without blocking the UI:
        /// it relies on OE's own asynchronous node expansion and yields between polls.
        /// (The public FindNode/SynchronizeTree enumerate synchronously on the UI thread, ~15 s freeze on a remote server.)
        /// </summary>
        public async System.Threading.Tasks.Task<bool> SelectDatabaseNodeAsync(HierarchyTreeNode root, string database, CollapseMode collapse, CancellationToken ct)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            IExplorerHierarchy hierarchy = root.Hierarchy;

            // 1. Server node finishes its async build (AddHierarchy already called Expand on it).
            if (!root.IsExpanded)
                root.Expand();
            if (!await WaitUntilAsync(() => !hierarchy.IsBuilding && root.Nodes.Count > 0 && Info(root.Nodes[0]) != null, ct))
            {
                Log.Error($"Server node {root.Text} did not finish expanding");
                return false;
            }
            TreeNode folder = FindChild(root, IsDatabasesFolder);
#if DEBUG
            Log.Info($"Children of {root.Text}: {DescribeChildren(root)}");
#endif
            if (folder == null)
            {
                Log.Error($"Databases folder not identified under {root.Text}; children: {DescribeChildren(root)}");
                return false;
            }

            // 2. Databases folder: expand (async build) and wait for the database node.
            if (!folder.IsExpanded)
                folder.Expand();
            Func<TreeNode, bool> isTarget = n => IsDatabase(n, database);
            if (!await WaitUntilAsync(() => !hierarchy.IsBuilding && FindChild(folder, isTarget) != null, ct))
            {
                Log.Error($"Database node '{database}' not found under {root.Text}/{folder.Text}");
                return false;
            }

            // 3. Select it, then collapse as configured. Collapsing an ancestor of the selected node makes
            //    WinForms move the selection to that ancestor.
            TreeNode dbNode = FindChild(folder, isTarget);
            dbNode.TreeView.SelectedNode = dbNode;
            switch (collapse)
            {
                case CollapseMode.Server:
                    root.Collapse(ignoreChildren: false);
                    break;
                case CollapseMode.Databases:
                    folder.Collapse(ignoreChildren: false);
                    break;
                default:
                    dbNode.EnsureVisible();
                    break;
            }
            return true;
        }

        private static readonly TimeSpan NodeWaitLimit = TimeSpan.FromSeconds(120);

        private static async System.Threading.Tasks.Task<bool> WaitUntilAsync(Func<bool> condition, CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow + NodeWaitLimit;
            while (true)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                if (condition())
                    return true;
                if (DateTime.UtcNow > deadline)
                    return false;
                await System.Threading.Tasks.Task.Delay(200, ct).ConfigureAwait(false);
            }
        }

        private static TreeNode FindChild(TreeNode parent, Func<TreeNode, bool> predicate) =>
            parent.Nodes.Cast<TreeNode>().FirstOrDefault(predicate);

        private static INodeInformation Info(TreeNode node) =>
            (node as IServiceProvider)?.GetService(typeof(INodeInformation)) as INodeInformation;

        // Verified on SSMS 18.10: the Databases folder has UrnPath "Server/DatabasesFolder", InvariantName "Databases"
        // (both non-localized); its children are "Server/Database" nodes.
        private static bool IsDatabasesFolder(TreeNode node)
        {
            INodeInformation info = Info(node);
            if (info == null)
                return false;
            return string.Equals(info.UrnPath, "Server/DatabasesFolder", StringComparison.OrdinalIgnoreCase)
                || string.Equals(info.InvariantName, "Databases", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDatabase(TreeNode node, string database)
        {
            INodeInformation info = Info(node);
            return info != null
                && info.UrnPath == "Server/Database"
                && string.Equals(info.InvariantName ?? info.Name, database, StringComparison.OrdinalIgnoreCase);
        }

        private static string DescribeChildren(TreeNode parent) =>
            string.Join("; ", parent.Nodes.Cast<TreeNode>().Select(n =>
            {
                INodeInformation i = Info(n);
                return i == null ? $"'{n.Text}' (no info)" : $"'{n.Text}' UrnPath={i.UrnPath} Invariant={i.InvariantName} Context={i.Context}";
            }));

        private object GetTree()
        {
            try
            {
                return treeProperty?.GetValue(oe);
            }
            catch (Exception ex)
            {
                Log.Error("Could not access Object Explorer tree", ex);
                return null;
            }
        }

        private static Type[] ParamTypes(MethodInfo m) => m.GetParameters().Select(p => p.ParameterType).ToArray();
    }
}
