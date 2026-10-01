# SsmsAutoConnect

An extension for **SQL Server Management Studio 18** that connects your preferred servers in Object Explorer when SSMS starts. Each connection uses your chosen database as its initial database, as if you had set *Options → Connect to database* in the Connect dialog. **New Query** on a server node then opens in that database.

- Connects every server listed in a small XML config, and the same server can be listed once per login (e.g. a read-only user and an admin).
- Skips connections that are already open in Object Explorer and collapses the new server nodes.
- Opens and validates connections in the background. An unreachable server or a bad login is logged and doesn't affect the others.
- Adds **Add to startup connections** to the right-click menu of database nodes in Object Explorer.
- Stores SQL-login passwords encrypted with Windows DPAPI for the current user, never in plain text.

Tested on SSMS 18.10 (15.0.18390). SSMS 18 has no public API for connecting Object Explorer, so the extension uses a few internal SSMS members via reflection. If those members are missing in another build, it falls back to the public API. The details are in [CLAUDE.md](CLAUDE.md).

## Requirements

- SSMS 18.x
- To build: Visual Studio 2019 (MSBuild) with .NET Framework 4.7.2; NuGet packages are restored automatically
- Administrator rights to deploy, because files are copied into the SSMS install folder

## Build and deploy

SSMS 18 doesn't install `.vsix` files, so the scripts copy the extension into SSMS's `Extensions` folder. Close SSMS, then run from an **elevated** PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy.ps1        # build, copy to SSMS, refresh its extension cache
powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -SkipBuild
powershell -ExecutionPolicy Bypass -File .\undeploy.ps1      # remove (your config is kept)
```

`build.ps1` builds without deploying and doesn't need administrator rights. The scripts assume SSMS is installed in the default location, `C:\Program Files (x86)\Microsoft SQL Server Management Studio 18`.

## Configure

The extension reads `%AppData%\SsmsAutoConnect\connections.xml`, and creates a sample on first start if the file is missing:

```xml
<Connections>
  <Connection>
    <Server>DEV</Server><Database>MyDb</Database>
    <UseWindowsAuth>true</UseWindowsAuth><UserName /><Password />
  </Connection>
  <Connection>
    <Server>DEV</Server><Database>MyDb</Database>
    <UseWindowsAuth>false</UseWindowsAuth><UserName>ro_user</UserName><Password>AQAAANCMnd8B...</Password>
  </Connection>
</Connections>
```

A connection is identified by **server + login**.

The easiest way to add entries is from SSMS: connect to a server, then right-click a database → **Add to startup connections**. If that server + login is already in the config, you'll be asked whether to replace its database.

For SQL logins written by hand, generate the `<Password>` value with the bundled tool, run as the same Windows user that runs SSMS:

```powershell
.\src\SsmsAutoConnect.Protect\bin\Release\SsmsAutoConnect.Protect.exe            # prompts, prints the encrypted value
.\src\SsmsAutoConnect.Protect\bin\Release\SsmsAutoConnect.Protect.exe --verify <value>
```

**Tip:** to stop SSMS's own Connect dialog from appearing at startup, set *Tools → Options → Environment → Startup → At startup* to **Open empty environment**. The extension shows Object Explorer when it connects.

## Troubleshooting

- `%AppData%\SsmsAutoConnect\autoconnect.log` records each start: what was connected or skipped, timings, and errors.
- Start SSMS with `Ssms.exe /log` to also write entries (source `SsmsAutoConnect`) to `%AppData%\Microsoft\AppEnv\15.0\ActivityLog.xml`.
- SSMS freezes for several seconds per server while it builds the server node. That is SSMS's own work and happens just the same when you connect manually.

## License

[MIT](LICENSE)
