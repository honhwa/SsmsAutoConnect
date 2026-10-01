using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
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

            // Attach on the UI thread in config order.
            foreach (var p in pending)
            {
                Prepared prepared;
                try
                {
                    prepared = await p.Task;
                }
                catch (Exception ex)
                {
                    Log.Error($"{p.Entry}: connection failed", ex);
                    continue;
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

                try
                {
                    bridge.AttachToObjectExplorer(prepared.ConnectionInfo, prepared.LiveConnection);
                    Log.Info($"{p.Entry}: connected in Object Explorer ({(bridge.UsesInternalConnect ? "internal" : "public")} route)");
                }
                catch (Exception ex)
                {
                    Log.Error($"{p.Entry}: adding to Object Explorer failed", ex);
                    continue;
                }

                if (string.IsNullOrEmpty(p.Entry.Database))
                    continue;
                try
                {
                    if (bridge.SelectDatabaseNode(p.Entry.Server.Trim(), p.Entry.Database))
                        Log.Info($"{p.Entry}: database node selected");
                }
                catch (Exception ex)
                {
                    Log.Error($"{p.Entry}: selecting database node failed", ex);
                }
            }
        }

        private sealed class Prepared
        {
            public UIConnectionInfo ConnectionInfo;
            public IDbConnection LiveConnection;
        }

        private static Prepared Prepare(ObjectExplorerBridge bridge, ConnectionEntry e)
        {
            UIConnectionInfo ci = BuildConnectionInfo(e);
            return new Prepared { ConnectionInfo = ci, LiveConnection = bridge.Validate(ci) };
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
