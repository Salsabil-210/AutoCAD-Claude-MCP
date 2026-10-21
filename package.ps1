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

# The ceiling feature skill, as a zip holding its folder, for upload in
# Claude Desktop (Settings > Capabilities > Skills).
$skillDirectory = Join-Path $projectDirectory "skills\ceiling-feature-production"
$skillZip = Join-Path $distributionDirectory "ceiling-feature-production-skill.zip"
if (Test-Path -LiteralPath $skillZip) {
    Remove-Item -LiteralPath $skillZip -Force
}
# Entries are named with forward slashes by hand: Windows PowerShell's
# Compress-Archive and ZipFile both write backslashes, which skill uploads
# may reject.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$skillParent = Split-Path -Parent $skillDirectory
$archive = [System.IO.Compression.ZipFile]::Open($skillZip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $skillDirectory -Recurse -File | ForEach-Object {
        $entryName = $_.FullName.Substring($skillParent.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $_.FullName,
            $entryName,
            [System.IO.Compression.CompressionLevel]::Optimal
        ) | Out-Null
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Created $skillZip"
