# AutoCAD Claude Bridge

An open-source, local MCP bridge that lets Claude Desktop inspect and automate the active AutoCAD 2027 drawing through a small allowlist of validated CAD tools.

> [!IMPORTANT]
> This is an independent community project. It is not affiliated with, endorsed by, or sponsored by Autodesk or Anthropic. AutoCAD is a trademark of Autodesk, Inc.; Claude is a trademark of Anthropic PBC.

## What it can do

Version 0.2.0 exposes sixteen Claude tools.

**Inspect**

- `autocad_status` — report the active drawing and its units without changing it.
- `list_entities` — list model-space entities with handle, type, layer, and key geometry. Read-only.
- `zoom_extents` — zoom the model-space view to the drawing extents. Does not change the drawing.

**Draw**

- `draw_line` — create one 2D line.
- `draw_rectangle` — create one axis-aligned closed rectangle.
- `draw_polyline` — create one open or closed 2D polyline.
- `add_hatch` — fill one closed polygon with a non-associative hatch (SOLID or any predefined pattern such as ANSI31).

**Annotate**

- `add_linear_dimension` — horizontal, vertical, or rotated dimension between two points.
- `add_aligned_dimension` — dimension parallel to the line between two points.
- `add_angular_dimension` — angle between two non-parallel lines.
- `add_text` — one multiline text (MText) object.
- `add_leader` — one multileader with arrow, leader line, and text.
- `set_dimension_props` — change text height, text gap, arrow size, text override, or color of one dimension.

**Edit**

- `move_entities` — move exact entities by handle.
- `erase_entities` — erase exact entities by handle.
- `delete_layer` — erase everything on one layer and purge it. Refuses layer 0, Defpoints, the current layer, locked layers, and layers still used by blocks.

The bridge intentionally does **not** expose arbitrary AutoCAD commands, AutoLISP evaluation, shell execution, or unrestricted file access. See the [roadmap](#roadmap) for planned geometry and editing tools.

## Units

Claude passes lengths exactly as you state them, tagged with their unit, and the plug-in converts them to the drawing's units (`INSUNITS`). Claude never converts units itself, so asking for "a 300 cm wall" sends `300` with `units: "cm"`, and the line comes out 300 cm long whether the drawing is in millimetres, centimetres, or metres.

- Supported units: `mm`, `cm`, `dm`, `m`, `km`, `in`, `ft`, `yd`, `mi`.
- Converted values: coordinates, widths and heights, offsets, move distances, text heights, and dimension text gap and arrow size. Angles stay in degrees.
- Results report lengths and measurements back in the requested unit, with a `units` label. `list_entities` can return geometry in any supported unit.
- `autocad_status` reports the drawing's `units`, `insunits`, `lunits`, `measurement`, `dimlfac`, and `dimscale`.
- **Unitless drawings** (`INSUNITS = 0`) refuse unit-tagged input, and Claude asks you what one drawing unit represents. Set `INSUNITS` in AutoCAD to enable conversion. Calls without `units` still use raw drawing units.
- **Hatch scale:** `patternScale` is a ratio and is never converted. When omitted, it is chosen from the drawing's units (for example 1 for mm, 0.1 for cm, 0.001 for m in a metric drawing), so patterns keep the same real-world spacing.
- Dimension text height and arrow size are still multiplied by the dimension style's `DIMSCALE` when AutoCAD displays them.

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

1. Download `AutoCAD-Claude-Bridge-0.2.0-win-x64.zip` from GitHub Releases.
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
- Coordinates, dimensions, units, and layer names are validated.
- Destructive tools act only on the exact handles or layer named; they never act on the current selection.
- Claude Desktop retains its tool-approval controls.
- Drawing changes use AutoCAD transactions and can be removed with `UNDO`.

Please report vulnerabilities privately as described in [SECURITY.md](SECURITY.md). Do not open a public issue containing exploit details or sensitive drawing data.

## Roadmap

- Circles and arcs
- Selection inspection
- Batch drawing plans with preview and grouped undo
- Copy, rotate, scale, offset, and trim
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
