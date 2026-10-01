using System;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Microsoft.SqlServer.Management;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SsmsAutoConnect
{
    /// <summary>
    /// Phase 2: "Add to startup connections" on Object Explorer database nodes.
    ///
    /// Uses the long-standing SSMS add-in pattern (public SqlWorkbench.Interfaces types, no reflection):
    /// on every OE selection change, take the selected database node's IMenuHandler (in SSMS 18.10 an
    /// ObjectExplorer.DefaultMenuHandler : HierarchyObject) and AddChild an IWinformsMenuHandler, whose
    /// GetMenuItems() DefaultMenuHandler calls when it builds the WinForms context menu. Handlers are cloned
    /// per node (children cloned via ICloneable), so we add to each handler instance once.
    /// </summary>
    internal sealed class StartupMenu
    {
        private const string MenuText = "Add to startup connections";

        private readonly AsyncPackage package;
        private readonly IObjectExplorerService oe;
        private readonly ConditionalWeakTable<object, object> extendedHandlers = new ConditionalWeakTable<object, object>();

        private StartupMenu(AsyncPackage package, IObjectExplorerService oe)
        {
            this.package = package;
            this.oe = oe;
        }

        /// <summary>UI thread only.</summary>
        public static void Register(AsyncPackage package, IObjectExplorerService oe, IContextService contextService)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var menu = new StartupMenu(package, oe);
            contextService.ActionContext.CurrentContextChanged += menu.OnContextChanged;
            Log.Info("Object Explorer menu command registered");
        }

        private void OnContextChanged(object sender, EventArgs e)
        {
            try
            {
                INodeInformation node = GetSelectedDatabaseNode();
                if (node == null)
                    return;
                if (node.GetService(typeof(IMenuHandler)) is HierarchyObject handler &&
                    !extendedHandlers.TryGetValue(handler, out _))
                {
                    handler.AddChild(string.Empty, new MenuItem(this));
                    extendedHandlers.Add(handler, null);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Adding Object Explorer menu item failed", ex);
            }
        }

        private INodeInformation GetSelectedDatabaseNode()
        {
            oe.GetSelectedNodes(out int count, out INodeInformation[] nodes);
            return count == 1 && nodes[0]?.UrnPath == "Server/Database" ? nodes[0] : null;
        }

        private void OnClick()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string title = "SsmsAutoConnect";
            try
            {
                INodeInformation node = GetSelectedDatabaseNode();
                if (node == null)
                {
                    Show(title, "Select a database node first.", OLEMSGICON.OLEMSGICON_WARNING);
                    return;
                }

                ConnectionEntry entry = BuildEntry(node, out string warning);
                bool updated = ConnectionConfig.AddOrUpdate(entry);
                Log.Info($"{entry}: {(updated ? "updated in" : "added to")} startup connections (node {node.Context})");

                string message = updated
                    ? $"Updated startup connection {entry.Server} ({Login(entry)}): database is now {entry.Database}."
                    : $"Added startup connection {entry.Server} ({Login(entry)}) / {entry.Database}.";
                if (warning != null)
                    message += Environment.NewLine + Environment.NewLine + warning;
                Show(title, message + Environment.NewLine + Environment.NewLine + ConnectionConfig.ConfigPath,
                     warning == null ? OLEMSGICON.OLEMSGICON_INFO : OLEMSGICON.OLEMSGICON_WARNING);
            }
            catch (Exception ex)
            {
                Log.Error("Add to startup connections failed", ex);
                Show(title, "Could not update startup connections: " + ex.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
            }
        }

        private static ConnectionEntry BuildEntry(INodeInformation node, out string warning)
        {
            warning = null;
            SqlOlapConnectionInfoBase connection = node.Connection;
            var entry = new ConnectionEntry
            {
                Server = connection.ServerName,   // as typed when connecting, e.g. "DEV"
                Database = node.Name,
                UseWindowsAuth = true,
            };

            if (connection is SqlConnectionInfo sql && !sql.UseIntegratedSecurity)
            {
                entry.UseWindowsAuth = false;
                entry.UserName = sql.UserName;
                string password = sql.Password;
                if (string.IsNullOrEmpty(password))
                    warning = "The password isn't available from this connection. Put the output of SsmsAutoConnect.Protect.exe into <Password> for this entry.";
                else
                    entry.Password = PasswordProtector.Protect(password);
            }
            return entry;
        }

        private static string Login(ConnectionEntry entry) => entry.UseWindowsAuth ? "Windows authentication" : "login " + entry.UserName;

        private void Show(string title, string message, OLEMSGICON icon) =>
            VsShellUtilities.ShowMessageBox(package, message, title, icon, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

        /// <summary>Child of a node's DefaultMenuHandler. Deliberately not an IMenuItem (those go through VS command plumbing).</summary>
        private sealed class MenuItem : HierarchyObject, IWinformsMenuHandler
        {
            private readonly StartupMenu owner;

            public MenuItem(StartupMenu owner) => this.owner = owner;

            public ToolStripItem[] GetMenuItems()
            {
                var item = new ToolStripMenuItem(MenuText);
                item.Click += (s, e) => owner.OnClick();
                return new ToolStripItem[] { item };
            }

            public override object Clone() => new MenuItem(owner);

            public override void AddChild(string name, object value)
            {
            }

            public override void AddProperty(string name, object value)
            {
            }
        }
    }
}
