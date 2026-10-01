# SsmsAutoConnect — SSMS 18.10 extension

## Task
On SSMS startup, connect every server listed in `%AppData%\SsmsAutoConnect\connections.xml`
in **Object Explorer** (no query windows), using the configured database as the connection's
initial database (Connect dialog → Options → "Connect to database"). Skip servers already connected in OE.
The server node is collapsed once SSMS finishes building it. The original spec also asked to expand Databases and select the DB
node; that was built, then **removed at the user's request (2026-10-01)**: "New Query" on the server node already
opens in the configured database, which is all the user needs (they confirmed it in SSMS).
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
  - `AutoConnector.cs`: dedupe, parallel off-UI-thread validation, attach in config order on the UI thread, then collapse each server node
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
  `"<ServerName as typed> (SQLServer, trusted)"` or `"... (SQLServer, user = X)"`. Used for dedupe.
- If ValidateConnection/private ConnectToServer aren't found: validate with our own SqlConnection off-thread, then call
  public `ConnectToServer(ci)`.

After attaching: `ObjectExplorerControl.AddHierarchy` selects and expands the new server root. Tree.SelectedNode is a
public `HierarchyTreeNode` whose `.Hierarchy.IsBuilding` is public. We poll every 200 ms (yielding the UI thread) until
it's no longer building, then `Collapse()`.

History: DB-node selection v1 used `FindNode(urn)` + `SynchronizeTree`. Those enumerate synchronously on the UI thread
(~15 s freeze on DEV). v2 used async expansion (Databases folder = `INodeInformation.UrnPath == "Server/DatabasesFolder"`,
DB = `UrnPath == "Server/Database"` + `InvariantName`; server URN uses the true name, e.g. typed `DEV` →
`Server[@Name='SQLHOST01']`). Both were removed with the feature (git history: commits 6b2116a..214af70).

## Measured (2026-10-01, DEV, tools/probe-startup.ps1)
- Validate (background): ~3.1 s. Not on the UI thread.
- Private `ConnectToServer(ci, conn, false)` on the UI thread: **~9.8 s, UI frozen**. This is SSMS's own code
  (GetHierarchy/BuildDataModel + AddHierarchy), the same path as connecting through the Connect dialog.
- SSMS's own startup is unresponsive for ~10 s before our package even loads; that isn't us.
- With a second server (QA), the warm second attach still took 9.6 s, so it's per-connection SSMS work, not
  first-connection cost. Preloading won't help, so we accept it (same as a manual connect).
