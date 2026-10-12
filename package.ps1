$ErrorActionPreference = "Stop"
$projectDirectory = $PSScriptRoot
$extensionDirectory = Join-Path $projectDirectory "claude-extension"
$distributionDirectory = Join-Path $projectDirectory "dist"
$temporaryZip = Join-Path $distributionDirectory "AutoCAD-Local-Bridge.zip"
$bundlePath = Join-Path $distributionDirectory "AutoCAD-Local-Bridge.mcpb"

if (!(Test-Path -LiteralPath $distributionDirectory)) {
    New-Item -ItemType Directory -Path $distributionDirectory | Out-Null
}

if (Test-Path -LiteralPath $temporaryZip) {
    Remove-Item -LiteralPath $temporaryZip -Force
}

if (Test-Path -LiteralPath $bundlePath) {
    Remove-Item -LiteralPath $bundlePath -Force
}

Compress-Archive -Path (Join-Path $extensionDirectory "*") `
    -DestinationPath $temporaryZip `
    -CompressionLevel Optimal
Move-Item -LiteralPath $temporaryZip -Destination $bundlePath

Write-Host "Created $bundlePath"
