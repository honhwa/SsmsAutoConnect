using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace SsmsAutoConnect
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("SsmsAutoConnect", "Auto-connects preferred servers in Object Explorer", "1.0")]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class SsmsAutoConnectPackage : AsyncPackage
    {
        public const string PackageGuidString = "92c430a5-3165-4c9b-a5ce-e049465bed94";

        private static readonly TimeSpan ObjectExplorerWaitLimit = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            Log.Info("Package initializing");
            // Don't hold up package load: the actual work runs as a separate joinable task.
            _ = JoinableTaskFactory.RunAsync(() => RunSafeAsync(cancellationToken));
            await Task.CompletedTask;
        }

        private async Task RunSafeAsync(CancellationToken ct)
        {
            try
            {
                IReadOnlyList<ConnectionEntry> entries = await Task.Run(() => ConnectionConfig.LoadOrCreateSample(), ct);
                int count = entries.Count;
                Log.Info($"Loaded {count} entr{(count == 1 ? "y" : "ies")} from {ConnectionConfig.ConfigPath}");
                if (count == 0)
                    return;

                IObjectExplorerService oe = await WaitForObjectExplorerAsync(ct);
                if (oe == null)
                {
                    Log.Error($"Object Explorer service not available after {ObjectExplorerWaitLimit.TotalSeconds:0}s; giving up");
                    return;
                }

                await JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                var bridge = new ObjectExplorerBridge(oe);
                await AutoConnector.RunAsync(bridge, entries, ct);
                Log.Info("Done");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error("Auto-connect failed", ex);
            }
        }

        private async System.Threading.Tasks.Task<IObjectExplorerService> WaitForObjectExplorerAsync(CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow + ObjectExplorerWaitLimit;
            while (DateTime.UtcNow < deadline)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                if (IsShellReady())
                {
                    var oe = (await GetServiceAsync(typeof(IObjectExplorerService)) as IObjectExplorerService)
                             ?? ServiceCache.ServiceProvider?.GetService(typeof(IObjectExplorerService)) as IObjectExplorerService;
                    if (oe != null)
                        return oe;
                }
                await System.Threading.Tasks.TaskScheduler.Default;
                await Task.Delay(PollInterval, ct);
            }
            return null;
        }

        private bool IsShellReady()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(GetService(typeof(SVsShell)) is IVsShell shell))
                return false;
            if (shell.GetProperty((int)__VSSPROPID.VSSPROPID_Zombie, out object zombie) == 0 && zombie is bool z && z)
                return false;
            return true;
        }
    }
}
