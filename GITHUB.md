# Publishing and installing on another computer

This is an independent MIT-licensed community project and is not affiliated with Autodesk or Anthropic. Do not include Autodesk API DLLs, AutoCAD installers, Claude credentials, drawings, or generated user data in the repository or release assets.

## What belongs in the repository

Commit the source files in this folder, including:

- `Plugin.cs`
- `AutoCADClaudeBridge.csproj`
- `NuGet.Config`
- `claude-extension/`
- `build.ps1`
- `package.ps1`
- `make-release.ps1`
- `packaging/`
- `README.md`
- `GITHUB.md`
- `.gitignore`

Do not commit `bin/`, `obj/`, `.build-state/`, or `dist/`. They are generated locally.

## Build requirements

- Windows x64
- AutoCAD 2027
- .NET 10 SDK
- PowerShell

From PowerShell in this repository folder:

```powershell
.\build.ps1
.\package.ps1
```

To create the one-click distribution ZIP instead, run:

```powershell
.\make-release.ps1
```

If AutoCAD is installed in a non-default location:

```powershell
.\build.ps1 -AutoCADInstallDir "D:\Autodesk\AutoCAD 2027"
```

## Release files

Attach these generated files to a GitHub Release:

```text
bin\Release\net10.0-windows\AutoCADClaudeBridge.dll
dist\AutoCAD-Local-Bridge.mcpb
dist\AutoCAD-Claude-Bridge-0.1.0-win-x64.zip
```

Users do not need the source code to run the bridge. They load the DLL with AutoCAD's `NETLOAD` command and install the MCPB from Claude Desktop's extension settings.

For the easiest installation, users download and extract the ZIP, close AutoCAD,
and double-click `Install.cmd`. This installs the AutoCAD autoload bundle for the
current Windows user and opens the Claude MCPB installer. Claude still requires
the user to review and approve the custom extension.

The installer generates a unique 256-bit local token at
`%LOCALAPPDATA%\AutoCADClaudeBridge\bridge-token.txt`. Never commit this file or
copy one computer's token into a release.

## Compatibility

The current build targets AutoCAD 2027 and .NET 10. Other AutoCAD releases can require a different target framework and matching Autodesk API references. Build and publish separate release artifacts for each supported AutoCAD major version.

The listener accepts connections only from `127.0.0.1`. Review the source before distributing it publicly, sign production releases, and do not add arbitrary command or shell execution tools.
