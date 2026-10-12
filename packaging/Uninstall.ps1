$ErrorActionPreference = "Stop"

$applicationPlugins = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins"
$targetBundle = Join-Path $applicationPlugins "AutoCADClaudeBridge.bundle"
$tokenDirectory = Join-Path $env:LOCALAPPDATA "AutoCADClaudeBridge"
$resolvedRoot = [System.IO.Path]::GetFullPath($applicationPlugins)
$resolvedTarget = [System.IO.Path]::GetFullPath($targetBundle)

if (!$resolvedTarget.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The AutoCAD plug-in target is outside the expected ApplicationPlugins folder."
}

if (Test-Path -LiteralPath $targetBundle) {
    Remove-Item -LiteralPath $targetBundle -Recurse -Force
    Write-Host "Removed AutoCADClaudeBridge.bundle."
} else {
    Write-Host "The AutoCAD bridge bundle was not installed for this user."
}

if (Test-Path -LiteralPath $tokenDirectory) {
    $resolvedLocalAppData = [System.IO.Path]::GetFullPath($env:LOCALAPPDATA)
    $resolvedTokenDirectory = [System.IO.Path]::GetFullPath($tokenDirectory)
    if (!$resolvedTokenDirectory.StartsWith($resolvedLocalAppData, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The token directory is outside the expected LocalAppData folder."
    }
    Remove-Item -LiteralPath $tokenDirectory -Recurse -Force
    Write-Host "Removed the per-user bridge token."
}

Write-Host "Remove AutoCAD Local Bridge separately from Claude Desktop Settings > Extensions."
Read-Host "Press Enter to close"
