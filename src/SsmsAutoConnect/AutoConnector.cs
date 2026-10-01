using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace SsmsAutoConnect
{
    internal static class AutoConnector
    {
        private static readonly Guid DatabaseEngineServerType = new Guid("8c91a03d-f9b4-46c0-a305-b5dcc79ff907");

        /// <summary>Called on the UI thread with Object Explorer available.</summary>
        public static async Task RunAsync(ObjectExplorerBridge bridge, IReadOnlyList<ConnectionEntry> entries, CancellationToken ct)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            HashSet<string> connected = bridge.GetConnectedServerNames() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toConnect = new List<ConnectionEntry>();
            foreach (ConnectionEntry e in entries)
            {
                if (string.IsNullOrWhiteSpace(e.Server))
                {
                    Log.Error("Config entry without <Server> skipped");
                    continue;
                }
                if (connected.Contains(e.Server.Trim()) || toConnect.Any(x => string.Equals(x.Server.Trim(), e.Server.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Info($"{e}: server already connected in Object Explorer (or listed twice), skipped");
                    continue;
                }
                toConnect.Add(e);
            }

            // Validate all servers in parallel off the UI thread; slow/unreachable servers don't block others.
            var pending = toConnect.Select(e => new
            {
                Entry = e,
                Task = Task.Run(() => Prepare(bridge, e), ct),
            }).ToList();

            // Attach on the UI thread in config order; collapsing then waits asynchronously for SSMS's node build.
            var collapses = new List<Task>();
            foreach (var p in pending)
            {
                Prepared prepared;
                try
                {
                    prepared = await p.Task;
                    Log.Info($"{p.Entry}: validated in {prepared.ValidateMs} ms (background)");
                }
                catch (Exception ex)
                {
                    Log.Error($"{p.Entry}: connection failed", ex);
                    continue;
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

                HierarchyTreeNode root;
                var sw = Stopwatch.StartNew();
                try
                {
                    bridge.AttachToObjectExplorer(prepared.ConnectionInfo, prepared.LiveConnection);
                    root = bridge.GetSelectedServerRoot();
                    Log.Info($"{p.Entry}: connected in Object Explorer ({(bridge.UsesInternalConnect ? "internal" : "public")} route), UI thread {sw.ElapsedMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    Log.Error($"{p.Entry}: adding to Object Explorer failed", ex);
                    continue;
                }

                if (root == null)
                    Log.Error($"{p.Entry}: server node not found after connecting; left expanded");
                else
                    collapses.Add(CollapseAsync(bridge, root, p.Entry, ct));
            }
            await Task.WhenAll(collapses);
        }

        private static async Task CollapseAsync(ObjectExplorerBridge bridge, HierarchyTreeNode root, ConnectionEntry entry, CancellationToken ct)
        {
            try
            {
                if (!await bridge.CollapseWhenBuiltAsync(root, ct))
                    Log.Error($"{entry}: server node still building after timeout; left expanded");
            }
            catch (Exception ex)
            {
                Log.Error($"{entry}: collapsing server node failed", ex);
            }
        }

        private sealed class Prepared
        {
            public UIConnectionInfo ConnectionInfo;
            public IDbConnection LiveConnection;
            public long ValidateMs;
        }

        private static Prepared Prepare(ObjectExplorerBridge bridge, ConnectionEntry e)
        {
            UIConnectionInfo ci = BuildConnectionInfo(e);
            var sw = Stopwatch.StartNew();
            IDbConnection conn = bridge.Validate(ci);
            return new Prepared { ConnectionInfo = ci, LiveConnection = conn, ValidateMs = sw.ElapsedMilliseconds };
        }

        private static UIConnectionInfo BuildConnectionInfo(ConnectionEntry e)
        {
            var ci = new UIConnectionInfo
            {
                ServerName = e.Server.Trim(),
                ServerType = DatabaseEngineServerType,
                PersistPassword = false,
            };
            if (e.UseWindowsAuth)
            {
                ci.AuthenticationType = 0;
                ci.UserName = string.Empty;
                ci.Password = string.Empty;
            }
            else
            {
                if (string.IsNullOrEmpty(e.UserName))
                    throw new InvalidOperationException("SQL login entry has no <UserName>");
                if (string.IsNullOrEmpty(e.Password))
                    throw new InvalidOperationException("SQL login entry has no <Password> (use SsmsAutoConnect.Protect.exe)");
                string password;
                try
                {
                    password = PasswordProtector.Unprotect(e.Password);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Could not decrypt <Password>; it must be produced by SsmsAutoConnect.Protect.exe as this Windows user", ex);
                }
                ci.AuthenticationType = 1;
                ci.UserName = e.UserName;
                ci.Password = password;
            }
            if (!string.IsNullOrEmpty(e.Database))
                ci.AdvancedOptions["DATABASE"] = e.Database;   // Connect dialog: Options > "Connect to database"
            return ci;
        }
    }
}
