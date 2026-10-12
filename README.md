# AutoCAD Claude Bridge

An open-source, local MCP bridge that lets Claude Desktop inspect and automate the active AutoCAD 2027 drawing through a small allowlist of validated CAD tools.

> [!IMPORTANT]
> This is an independent community project. It is not affiliated with, endorsed by, or sponsored by Autodesk or Anthropic. AutoCAD is a trademark of Autodesk, Inc.; Claude is a trademark of Anthropic PBC.

## What it can do

Version 0.1.0 exposes three Claude tools:

- `autocad_status` — report the active drawing without changing it.
- `draw_line` — create one validated 2D line.
- `draw_rectangle` — create one validated axis-aligned closed rectangle.

The bridge intentionally does **not** expose arbitrary AutoCAD commands, AutoLISP evaluation, shell execution, or unrestricted file access. See the [roadmap](#roadmap) for planned geometry and editing tools.

## Architecture

```text
Claude Desktop
  -> packaged Node.js MCP server
  -> newline-delimited JSON over 127.0.0.1:51327
  -> AutoCAD .NET plug-in
  -> active DWG
```

All AutoCAD API work is queued onto AutoCAD's main thread. Each installation receives a random 256-bit local token stored at:

```text
%LOCALAPPDATA%\AutoCADClaudeBridge\bridge-token.txt
```

## Requirements

### Run a release

- Windows x64
- AutoCAD 2027
- Claude Desktop for Windows

### Build from source

- The runtime requirements above
- .NET 10 SDK
- PowerShell
- VS Code is sufficient; Visual Studio is optional

Users must obtain their own properly licensed copies of AutoCAD and Claude Desktop. Autodesk binaries are not included in this repository.

## Install a release

1. Download `AutoCAD-Claude-Bridge-0.1.0-win-x64.zip` from GitHub Releases.
2. Extract the ZIP to a local folder.
3. Close AutoCAD.
4. Double-click `Install.cmd`.
5. Review and approve **AutoCAD Local Bridge** in Claude Desktop.
6. Start AutoCAD 2027 and open a drawing.
7. Type `CLAUDEBRIDGE` in AutoCAD.
8. In Claude Desktop, ask: `Use autocad_status to check my active drawing. Do not draw anything.`

For cautious testing, choose **Allow once** when Claude requests tool permission.

## Build

From PowerShell in the repository folder:

```powershell
.\build.ps1
.\package.ps1
```

Create the complete distributable ZIP:

```powershell
.\make-release.ps1
```

For a non-default AutoCAD location:

```powershell
.\build.ps1 -AutoCADInstallDir "D:\Autodesk\AutoCAD 2027"
```

Generated files appear under `bin\Release` and `dist`. These folders are intentionally excluded from source control.

## Security

- The listener binds only to `127.0.0.1`.
- A unique token is generated for each Windows user installation.
- Only explicitly implemented tools can be called.
- Coordinates, dimensions, and layer names are validated.
- Claude Desktop retains its tool-approval controls.
- Drawing changes use AutoCAD transactions and can be removed with `UNDO`.

Please report vulnerabilities privately as described in [SECURITY.md](SECURITY.md). Do not open a public issue containing exploit details or sensitive drawing data.

## Roadmap

- Circles, arcs, polylines, text, dimensions, and hatches
- Read-only drawing and selection inspection
- Batch drawing plans with preview and grouped undo
- Move, copy, rotate, scale, offset, trim, and handle-based deletion
- Blocks, reusable shelving/cabinet generators, and fabrication outputs
- Per-version AutoCAD builds, code signing, checksums, and CI validation
- A separate SketchUp Ruby bridge using the same MCP safety model

## Documentation

- [Installation and operations guide](documentation/AutoCAD-Claude-Bridge-Documentation.docx)
- [GitHub publishing and compatibility notes](GITHUB.md)
- [Contribution guide](CONTRIBUTING.md)
- [Security policy](SECURITY.md)

## Contributing

Issues and pull requests are welcome. Keep new tools narrow, typed, validated, reversible where possible, and covered by tests. Read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting changes.

## License

Licensed under the [MIT License](LICENSE).
