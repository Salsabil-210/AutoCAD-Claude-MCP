$ErrorActionPreference = "Stop"

$sourceBundle = Join-Path $PSScriptRoot "AutoCADClaudeBridge.bundle"
$claudeExtension = Join-Path $PSScriptRoot "AutoCAD-Local-Bridge.mcpb"
$applicationPlugins = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins"
$targetBundle = Join-Path $applicationPlugins "AutoCADClaudeBridge.bundle"
$tokenDirectory = Join-Path $env:LOCALAPPDATA "AutoCADClaudeBridge"
$tokenFile = Join-Path $tokenDirectory "bridge-token.txt"

if (!(Test-Path -LiteralPath $sourceBundle)) {
    throw "AutoCADClaudeBridge.bundle is missing from the installer folder."
}

if (!(Test-Path -LiteralPath $claudeExtension)) {
    throw "AutoCAD-Local-Bridge.mcpb is missing from the installer folder."
}

if (!(Test-Path -LiteralPath $applicationPlugins)) {
    New-Item -ItemType Directory -Path $applicationPlugins | Out-Null
}

$resolvedRoot = [System.IO.Path]::GetFullPath($applicationPlugins)
$resolvedTarget = [System.IO.Path]::GetFullPath($targetBundle)
if (!$resolvedTarget.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The AutoCAD plug-in destination is outside the expected ApplicationPlugins folder."
}

if (Test-Path -LiteralPath $targetBundle) {
    Remove-Item -LiteralPath $targetBundle -Recurse -Force
}

Copy-Item -LiteralPath $sourceBundle -Destination $targetBundle -Recurse

if (!(Test-Path -LiteralPath $tokenDirectory)) {
    New-Item -ItemType Directory -Path $tokenDirectory | Out-Null
}

if (!(Test-Path -LiteralPath $tokenFile)) {
    $tokenBytes = New-Object byte[] 32
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($tokenBytes)
    } finally {
        $generator.Dispose()
    }
    $token = -join ($tokenBytes | ForEach-Object { $_.ToString("x2") })
    [System.IO.File]::WriteAllText($tokenFile, $token, [System.Text.UTF8Encoding]::new($false))
}

Write-Host "AutoCAD autoload plug-in installed at:"
Write-Host $targetBundle
Write-Host ""
Write-Host "Opening the Claude extension installer. Review and approve it in Claude Desktop."

Start-Process -FilePath $claudeExtension

Write-Host ""
Write-Host "Restart AutoCAD, open a drawing, and type CLAUDEBRIDGE to verify the connection."
Read-Host "Press Enter to close"
