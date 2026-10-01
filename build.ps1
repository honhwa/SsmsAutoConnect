# Builds SsmsAutoConnect (VSIX package + Protect console tool) with VS 2019 MSBuild.
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found via vswhere' }

foreach ($proj in 'src\SsmsAutoConnect\SsmsAutoConnect.csproj', 'src\SsmsAutoConnect.Protect\SsmsAutoConnect.Protect.csproj') {
    & $msbuild (Join-Path $PSScriptRoot $proj) /restore /t:Build /p:Configuration=$Configuration /p:Platform=AnyCPU /nologo /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $proj" }
}
