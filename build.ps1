param(
    [string]$AutoCADInstallDir = "C:\Program Files\Autodesk\AutoCAD 2027"
)

$ErrorActionPreference = "Stop"
$projectDirectory = $PSScriptRoot
$localBuildState = Join-Path $projectDirectory ".build-state"
$localAppData = Join-Path $localBuildState "appdata"
$localDotnetHome = Join-Path $localBuildState "dotnet-home"

$env:APPDATA = $localAppData
$env:DOTNET_CLI_HOME = $localDotnetHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

dotnet restore (Join-Path $projectDirectory "AutoCADClaudeBridge.csproj") `
    --configfile (Join-Path $projectDirectory "NuGet.Config") `
    -p:AutoCADInstallDir="$AutoCADInstallDir"

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet build (Join-Path $projectDirectory "AutoCADClaudeBridge.csproj") `
    --no-restore `
    --configuration Release `
    -p:AutoCADInstallDir="$AutoCADInstallDir"

exit $LASTEXITCODE
