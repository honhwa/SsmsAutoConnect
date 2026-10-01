using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.SqlServer.Management.Common;
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
        /// Connections currently in Object Explorer, as <see cref="ConnectionEntry.MakeKey"/> keys (server as typed + login).
        /// Returns null if the internal tree isn't accessible. UI thread only.
        /// </summary>
        public HashSet<string> GetConnectedKeys()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            object tree = GetTree();
            if (tree == null)
                return null;

            PropertyInfo hierarchiesProperty = tree.GetType().GetProperty("Hierarchies", InstanceNonPublic | BindingFlags.Public);
            if (!(hierarchiesProperty?.GetValue(tree) is IDictionary hierarchies))
                return null;

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object key in hierarchies.Keys)
            {
                if (key is string s && ParseHierarchyKey(s) is string k)
                    keys.Add(k);
            }
            return keys;
        }

        /// <summary>
        /// Hierarchy keys come from SharedConnectionUtil.GetConnectionKeyName:
        ///   "DEV (SQLServer, trusted)"            → Windows auth
        ///   "DEV (SQLServer, user = ro_user)"     → SQL login (Azure adds ", &lt;db&gt;" / ", tenant = ...")
        /// Non-SQL Server connections (OLAP, SSIS, ...) return null.
        /// </summary>
        internal static string ParseHierarchyKey(string key)
        {
            int paren = key.IndexOf(" (", StringComparison.Ordinal);
            if (paren <= 0 || !key.EndsWith(")", StringComparison.Ordinal))
                return null;
            string server = key.Substring(0, paren);
            string rest = key.Substring(paren + 2, key.Length - paren - 3);   // "SQLServer, trusted"
            if (!rest.StartsWith("SQLServer", StringComparison.Ordinal))
                return null;

            const string userPrefix = ", user = ";
            int u = rest.IndexOf(userPrefix, StringComparison.Ordinal);
            if (u < 0)
                return ConnectionEntry.MakeKey(server, null);
            string login = rest.Substring(u + userPrefix.Length);
            int comma = login.IndexOf(", ", StringComparison.Ordinal);
            return ConnectionEntry.MakeKey(server, comma >= 0 ? login.Substring(0, comma) : login);
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
        /// Collapses the server node once SSMS has finished its own asynchronous build of it
        /// (AddHierarchy expands every newly added server). Yields the UI thread while waiting.
        /// </summary>
        public async System.Threading.Tasks.Task<bool> CollapseWhenBuiltAsync(HierarchyTreeNode root, CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow + BuildWaitLimit;
            while (true)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                if (!root.Hierarchy.IsBuilding)
                {
                    root.Collapse(ignoreChildren: false);
                    return true;
                }
                if (DateTime.UtcNow > deadline)
                    return false;
                await System.Threading.Tasks.Task.Delay(200, ct).ConfigureAwait(false);
            }
        }

        private static readonly TimeSpan BuildWaitLimit = TimeSpan.FromSeconds(120);

        /// <summary>
        /// The custom connection color of an Object Explorer connection, as "#RRGGBB", or null if it has none or it can't
        /// be read. Nodes only expose the core SqlOlapConnectionInfoBase; the color lives in the UIConnectionInfo that OE
        /// keeps in its internal ConnectionCache (ObjectExplorer.dll):
        ///   internal static UIConnectionInfo ConnectionCache.GetUIConnectionInfo(SqlOlapConnectionInfoBase)
        /// </summary>
        public static string TryGetCustomColor(SqlOlapConnectionInfoBase connection)
        {
            try
            {
                Type cacheType = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "ObjectExplorer")
                    ?.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.ConnectionCache");
                MethodInfo getUi = cacheType?.GetMethod("GetUIConnectionInfo", StaticNonPublic | BindingFlags.Public,
                    null, new[] { typeof(SqlOlapConnectionInfoBase) }, null);
                if (getUi == null)
                {
                    Log.Error("ConnectionCache.GetUIConnectionInfo not found; connection color not captured");
                    return null;
                }
                if (!(getUi.Invoke(null, new object[] { connection }) is UIConnectionInfo ci))
                    return null;
                if (!UIConnectionInfoUtil.GetUseCustomConnectionColor(ci))
                    return null;
                return ConnectionEntry.FormatColor(UIConnectionInfoUtil.GetCustomConnectionColor(ci));
            }
            catch (Exception ex)
            {
                Log.Error("Reading connection color failed", ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex);
                return null;
            }
        }

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
