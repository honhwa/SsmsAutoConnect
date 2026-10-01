# SsmsAutoConnect — SSMS 18.10 extension

## Task
On SSMS startup, connect every server listed in `%AppData%\SsmsAutoConnect\connections.xml`
in **Object Explorer** (no query windows), using the configured database as the connection's
initial database (Connect dialog → Options → "Connect to database"), then expand the
Databases folder and select that database node. Skip servers already connected in OE.
Passwords are stored DPAPI-encrypted (CurrentUser). Failures go to the ActivityLog
(`ActivityLog.LogError("SsmsAutoConnect", ...)`), and one bad entry never blocks the others.

Phase 2 (only after the user confirms Phase 1): an OE context-menu command on database nodes,
"Add to startup connections", that appends the server and database to the config.

## Verified environment (2026-10-01)
- SSMS 18.10: `C:\Program Files (x86)\Microsoft SQL Server Management Studio 18\Common7\IDE\Ssms.exe`, version 15.0.18390.0
- VS 2019 Community 16.11, MSBuild: `C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe`
  (vswhere does not report the VSSDK workload; build targets come from the `Microsoft.VSSDK.BuildTools` NuGet package)
- dotnet SDK 9.0.200 (used only for `ilspycmd` and nothing else)
- The Claude session is NOT elevated, so the user runs deploy/undeploy from an elevated PowerShell.
- Existing extensions in `IDE\Extensions`: Application, SolarWinds Plan Explorer, SQLPrompt, SSMSPlus. Don't touch them.
- SSMS assemblies (all in `IDE\` root unless noted; reference with Private=False):
  SqlWorkbench.Interfaces.dll, SqlPackageBase.dll, ObjectExplorer.dll, Microsoft.SqlServer.RegSvrEnum.dll,
  Microsoft.SqlServer.ConnectionInfo.dll, Microsoft.SqlServer.Management.Sdk.SqlStudio.dll,
  Microsoft.SqlServer.Management.Sdk.Sfc.dll, ConnectionDlg.dll,
  `Extensions\Application\Microsoft.SqlServer.Management.SqlStudio.Explorer.dll`

## Layout
- `src/SsmsAutoConnect/`: the VSIX package (classic csproj, net472, AnyCPU)
  - `SsmsAutoConnectPackage.cs`: AsyncPackage, background auto-load on NoSolution; polls (500 ms, max 60 s) for
    shell-not-zombie + `IObjectExplorerService`, then runs AutoConnector
  - `AutoConnector.cs`: dedupe, parallel off-UI-thread validation, attach + select in config order on the UI thread
  - `ObjectExplorerBridge.cs`: **all reflection into SSMS internals lives here**
  - `ConnectionConfig.cs`: XML config (XDocument, no XmlSerializer), `PasswordProtector.cs` DPAPI, `Log.cs`
- `src/SsmsAutoConnect.Protect/`: console tool that prints the DPAPI blob for `<Password>` (links PasswordProtector.cs)
- `build.ps1`: MSBuild via vswhere, `/restore`. `deploy.ps1` / `undeploy.ps1` need elevation.

## Decisions
- Classic VSIX project, net472, AnyCPU. `Microsoft.VisualStudio.SDK` 15.9.3 (ExcludeAssets=runtime),
  `Microsoft.VSSDK.BuildTools` 15.9.3086. SSMS redirects Microsoft.VisualStudio.Threading to 15.8.0.0, matching the SDK.
- `<UseCodebase>true</UseCodebase>` is required: without it the pkgdef has no CodeBase and SSMS can't find the unsigned DLL.
- Compile-time refs (Private=False, HintPath = `$(SsmsIdeDir)`): SqlWorkbench.Interfaces, SqlPackageBase, ConnectionDlg,
  Microsoft.SqlServer.RegSvrEnum, Microsoft.SqlServer.ConnectionInfo. No reference to ObjectExplorer.dll or
  SqlStudio.Explorer.dll; those are reached only via reflection.
- Deploy by copying manifest + dll + pkgdef into `IDE\Extensions\SsmsAutoConnect\`, then `Ssms.exe /setup`.
- Dedupe is by server name (case-insensitive, as typed) against the OE hierarchy keys, regardless of login.
- Diagnostics: ActivityLog (only with `Ssms.exe /log`, at `%AppData%\Microsoft\AppEnv\15.0\ActivityLog.xml`) plus
  `%AppData%\SsmsAutoConnect\autoconnect.log` (always written, reset on each SSMS start).
- Passwords: DPAPI CurrentUser with entropy "SsmsAutoConnect.v1", base64 in `<Password>`.

## Internal SSMS APIs relied on (SSMS 18.10, decompiled with ilspycmd 9.1)
Public, documented-ish (SqlWorkbench.Interfaces.dll), `IObjectExplorerService` (GUID 26E139FB-...):
`ConnectToServer(object)`, `GetSelectedNodes`, `FindNode(urn)`, `SynchronizeTree(INodeInformation)`.
Obtained via `AsyncPackage.GetServiceAsync(typeof(IObjectExplorerService))`, fallback `ServiceCache.ServiceProvider`.

Implementation: `Microsoft.SqlServer.Management.SqlStudio.Explorer.ObjectExplorerService`
(`IDE\Extensions\Application\Microsoft.SqlServer.Management.SqlStudio.Explorer.dll`).
- Public `ConnectToServer(UIConnectionInfo)` → private `ConnectToServer(ci, null, validateConnection:true)`, which
  opens the connection **on the UI thread** and shows a **message box** on failure. Unsuitable at startup, so it is the fallback only.
- **internal static `IDbConnection ValidateConnection(UIConnectionInfo, IServerType)`**: opens and validates the connection,
  sets ApplicationName, checks DAC/SMO XPs. We call it on a thread-pool thread. `IServerType` comes from
  public `UIConnectionInfoUtil.GetServerType(ci)` (ConnectionDlg.dll).
- **private `void ConnectToServer(UIConnectionInfo, IDbConnection liveConnection, bool validateConnection)`**: called with
  `(ci, openConn, false)` on the UI thread. This is exactly what SSMS's own Connect dialog path (`NewConnection`) does.
  It builds core info via `UIConnectionInfoUtil.GetCoreConnectionInfo(ci, includeDatabase:true)`, which sets
  `DatabaseName` from `ci.AdvancedOptions["DATABASE"]`. That is how the initial database takes effect.
- **internal property `Tree`** (ObjectExplorerControl, a TreeView) → **internal `Hierarchies`**
  (`OEHierarchies : Dictionary<string, IExplorerHierarchy>`). Keys = `SharedConnectionUtil.GetConnectionKeyName`:
  `"<ServerName as typed> (SQLServer, trusted)"` or `"... (SQLServer, user = X)"`. Used for dedupe, and to collapse the
  selected DB node after SynchronizeTree.
- If ValidateConnection/private ConnectToServer aren't found: validate with our own SqlConnection off-thread, then call
  public `ConnectToServer(ci)`.

Node selection: `ObjectExplorerControl.AddHierarchy` selects and expands the new server root, so right after attaching,
`GetSelectedNodes()[0].Context` is the server URN with the server's *true* name (e.g. `Server[@Name='PC\SQL2019']`).
We append `/Database[@Name='db']` (quotes doubled), then `FindNode(urn)` (NotifyHandler.FindItem walks the navigable
model, enumerating synchronously) and `SynchronizeTree(node)` (expands parents, selects, and expands the node; we collapse it).
