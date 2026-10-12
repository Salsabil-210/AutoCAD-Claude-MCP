$ErrorActionPreference = "Stop"
$projectDirectory = $PSScriptRoot
$distributionDirectory = Join-Path $projectDirectory "dist"
$releaseDirectory = Join-Path $distributionDirectory "AutoCAD-Claude-Bridge-0.1.0-win-x64"
$bundleDirectory = Join-Path $releaseDirectory "AutoCADClaudeBridge.bundle"
$bundleContents = Join-Path $bundleDirectory "Contents\Windows"
$releaseZip = Join-Path $distributionDirectory "AutoCAD-Claude-Bridge-0.1.0-win-x64.zip"

& (Join-Path $projectDirectory "build.ps1")
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& (Join-Path $projectDirectory "package.ps1")
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (Test-Path -LiteralPath $releaseDirectory) {
    Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $releaseZip) {
    Remove-Item -LiteralPath $releaseZip -Force
}

New-Item -ItemType Directory -Path $bundleContents -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectDirectory "packaging\PackageContents.xml") `
    -Destination (Join-Path $bundleDirectory "PackageContents.xml")
Copy-Item -LiteralPath (Join-Path $projectDirectory "bin\Release\net10.0-windows\AutoCADClaudeBridge.dll") `
    -Destination (Join-Path $bundleContents "AutoCADClaudeBridge.dll")
Copy-Item -LiteralPath (Join-Path $distributionDirectory "AutoCAD-Local-Bridge.mcpb") `
    -Destination (Join-Path $releaseDirectory "AutoCAD-Local-Bridge.mcpb")
Copy-Item -LiteralPath (Join-Path $projectDirectory "packaging\Install.ps1") `
    -Destination $releaseDirectory
Copy-Item -LiteralPath (Join-Path $projectDirectory "packaging\Install.cmd") `
    -Destination $releaseDirectory
Copy-Item -LiteralPath (Join-Path $projectDirectory "packaging\Uninstall.ps1") `
    -Destination $releaseDirectory
Copy-Item -LiteralPath (Join-Path $projectDirectory "packaging\Uninstall.cmd") `
    -Destination $releaseDirectory
Copy-Item -LiteralPath (Join-Path $projectDirectory "packaging\INSTALL.txt") `
    -Destination $releaseDirectory

Compress-Archive -Path (Join-Path $releaseDirectory "*") `
    -DestinationPath $releaseZip `
    -CompressionLevel Optimal

Write-Host "Created release package:"
Write-Host $releaseZip
