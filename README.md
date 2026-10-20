# AutoCAD Claude Bridge

An open-source, local MCP bridge that lets Claude Desktop inspect and automate the active AutoCAD 2027 drawing through a small allowlist of validated CAD tools.

> [!IMPORTANT]
> This is an independent community project. It is not affiliated with, endorsed by, or sponsored by Autodesk or Anthropic. AutoCAD is a trademark of Autodesk, Inc.; Claude is a trademark of Anthropic PBC.

## What it can do

The bridge exposes thirty-one Claude tools.

**Inspect**

- `autocad_status` — report the active drawing and its units without changing it.
- `list_entities` — list model-space entities with handle, type, layer, bounding box, and key geometry, including block name, position, rotation, and attributes. Read-only.
- `zoom_extents` — zoom the model-space view to the drawing extents. Does not change the drawing.

**Floor Plan**

- `floor_plan_summary` — compact overview of the floor plan: saved plan type, extents with width and height, and counts by layer, type, and block name. Read-only.
- `query_region` — list entities whose bounding box overlaps (`crossing`) or lies within (`inside`) a rectangle. Read-only.
- `set_floor_plan_type` — save the plan type (`architectural`, `furniture`, `electrical`, `plumbing`, `reflected_ceiling`, `other`) in the drawing's Named Objects Dictionary under `CLAUDE_BRIDGE_FLOOR_PLAN`. It is saved with the DWG and can be removed with `UNDO`.
- `detect_rooms` — find rooms as the closed areas formed by walls on the layers you name. Read-only. See [Room detection](#room-detection).
- `save_rooms` — run room detection and save the rooms in the DWG under `CLAUDE_BRIDGE_ROOMS`, keeping the IDs of rooms saved before. Can be removed with `UNDO`. See [Saved rooms](#saved-rooms-and-room-schedule).
- `get_rooms` — return the saved rooms, each checked against the current walls and marked `stale` when they changed. Read-only.
- `export_room_schedule` — write the saved rooms as a CSV room schedule (areas in m², perimeter in m, status, warnings).

## Room detection

`detect_rooms` turns wall linework into rooms:

1. Lines, polylines (including arc segments), arcs and circles on `wallLayers` are split at every crossing and T-junction.
2. Open wall ends that face each other along the wall line and are at most `maxOpeningWidth` apart (default 1.2 m) are joined as **openings**, so a doorway does not merge two rooms. Wall ends at most `maxDraftingGap` apart (default 2 cm) are joined in any direction as **drafting gaps**.
3. Any wall end still open is reported in `openWallEnds` with its point and source handle.
4. Every enclosed area becomes a room. Areas narrower than `minRoomWidth` (default 0.5 m), such as the space between the two lines of a wall, or smaller than `minRoomArea` (default 1 m²) are dropped. A separate wall group inside a room, such as a column, is subtracted as a hole.
5. Text, multiline text and room-tag blocks (attribute `NAME`, `ROOM`, `ROOM_NAME`, `ROOMNAME`, `ROOM_NO` or `ROOMNO`) inside a room become its labels, and blocks inside it are listed and counted.

Limits:

- Doorways drawn with capped jambs (a short line closing each wall end) leave no open ends, so the two rooms come back as one with a warning when it holds several labels. Draw a room separation line across the opening on a wall layer.
- Walls inside blocks or xrefs are not read; their types are counted in `ignoredOnWallLayers`.
- Up to 5,000 wall segments per call.

## Saved rooms and room schedule

`save_rooms` takes the same arguments as `detect_rooms`, runs detection, and stores one record per room in the drawing. `get_rooms` reads them back and `export_room_schedule` writes them to a CSV file.

### Room record

| Field | Meaning |
| --- | --- |
| `id` | `R1`, `R2`, … Stable across re-saves; numbers are never reused. |
| `name` | The room's label when exactly one label lies inside it, otherwise empty. |
| `status` | `ok` (detected cleanly), `needs_review` (several labels inside: probably two rooms joined through an opening), `confirmed` (checked by the user). `get_rooms` and the schedule also report `stale` (see below); `stale` is never stored. |
| `warnings` | Why a room needs review. |
| `boundary` | Outline vertices, counter-clockwise; `holes` are the outlines of columns or shafts inside it. |
| `netArea`, `grossArea` | Gross area is the area inside the outline; net area is gross area minus holes. |
| `perimeter` | Length of the outline, holes excluded. |
| `units`, `metresPerUnit` | Drawing units (`INSUNITS`) at save time and their size in metres (empty in unitless drawings). Geometry is stored in drawing units; tools return it in the requested `units` plus square metres. |
| `measurementConvention` | The convention above, stored with the record. |
| `sourceWalls` | Handle and geometry fingerprint of every wall entity the outline runs along. |
| `labelHandles` | Handles of the labels inside the room. |
| `detectionVersion` | Version of the detection that produced the record (`detect_rooms/1`). |
| `createdAt`, `updatedAt` | UTC timestamps of the first save and of the latest save. |

### Storage

Records live in a dictionary `CLAUDE_BRIDGE_ROOMS` in the Named Objects Dictionary: one Xrecord per room ID holding the record as JSON (schema 1, split into 250-character strings), and a `META` Xrecord with the next room number. Every save is one transaction, so `UNDO` reverts it, and the records travel with the DWG.

### Re-saving and confirmation

- A detected room keeps a saved room's ID when they share a label name that is unique on both sides, otherwise when their outlines overlap by at least half of the larger one. Each saved ID is used at most once, so re-saving never duplicates rooms. Unmatched detected rooms get new IDs; saved rooms no longer detected are removed and listed in `removedRoomIds`.
- Rooms that need review are saved with status `needs_review` and their warnings, never as `ok` or `confirmed`.
- `confirmRoomIds` marks rooms `confirmed` once the user has checked them. It is refused, and nothing is saved, for a `needs_review` room, an unknown ID, or a plan with open wall ends.
- A confirmed room stays confirmed on a re-save only when its name, walls, wall geometry and area are unchanged; otherwise it drops back to `ok` or `needs_review`.

### Staleness

`get_rooms` and `export_room_schedule` look up every source wall handle and recompute its fingerprint (a hash of its layer and geometry). A room whose walls were erased, moved, reshaped or put on another layer, or whose drawing units changed, is reported as `stale` with `staleReasons`; its `savedStatus` is kept. Run `save_rooms` again to update it. New walls that do not touch a room's saved walls are not seen as changes, so re-save after editing the plan.

### Room schedule

`export_room_schedule` writes UTF-8 CSV with the columns `Room ID, Name, Status, Net area (m²), Gross area (m²), Perimeter (m), Warnings`, values rounded to 0.01. The status is written as it is, so `needs_review` and `stale` rooms are never shown as confirmed, and warnings include stale reasons. The file goes to `path` (a full path ending in `.csv`) or next to the saved DWG as `<drawing>-rooms.csv`. An existing file is replaced only with `overwrite: true`. Writing the file is not undone by `UNDO`. Text a spreadsheet would read as a formula is written as plain text. Rooms saved in a unitless drawing cannot be exported in m².

**Draw**

- `draw_line` — create one 2D line.
- `draw_rectangle` — create one axis-aligned closed rectangle.
- `draw_polyline` — create one open or closed 2D polyline.
- `add_hatch` — fill one closed polygon with a non-associative hatch (SOLID or any predefined pattern such as ANSI31).
- `draw_circle` — create one circle.
- `draw_arc` — create one counter-clockwise arc between two angles.
- `draw_walls` — double-line walls along a centreline path with mitred corners, plus doors, windows and openings. See [Drawing a floor plan](#drawing-a-floor-plan).
- `place_furniture` — place a furniture or fixture symbol (bed, sofa, WC, basin, bathtub, stove, ...) as a block.
- `insert_block` — insert a block defined in the drawing or taken from the bridge block library, with attribute values.

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
- `copy_entities` — copy exact entities by handle, once or as an evenly spaced row.
- `rotate_entities` — rotate exact entities about a base point.
- `mirror_entities` — mirror exact entities about a line, as copies or in place.
- `delete_layer` — erase everything on one layer and purge it. Refuses layer 0, Defpoints, the current layer, locked layers, and layers still used by blocks.

The bridge intentionally does **not** expose arbitrary AutoCAD commands, AutoLISP evaluation, shell execution, or unrestricted file access. See the [roadmap](ROADMAP.md) for planned tools.

## Drawing a floor plan

Claude draws a furnished plan by calling the small tools in order and checking each result:

1. **Exterior walls** — `draw_walls` with the outline as a closed centreline path, for example `thickness: 0.2`, `units: "m"`.
2. **Partitions** — `draw_walls` with open paths, for example `thickness: 0.1`. End each partition at the face of the wall it meets: half that wall's thickness from its centreline.
3. **Doors, windows, openings** — `openings` on the wall that holds them: `segment` (0 is the segment from the first point to the second), `offset` from that segment's start to the near edge of the opening, `width`, `kind`, and for doors `hinge` (`start` or `end` of the opening) and `swing` (`left` or `right` of the path direction). Doors and openings break the wall lines and get jamb caps and a centreline room separation line. Windows keep the wall lines, as the wall below the sill, and add jamb and glazing lines, so they do not change room areas.
4. **Furniture and fixtures** — `place_furniture` with a `kind`, a centre point and a rotation. The back of each symbol faces +Y at rotation 0; use 90, 180 or -90 for a wall on the left, below or on the right. Repeat chairs with `copy_entities` or `rotate_entities`.
5. **Labels and dimensions** — `add_text` on `A-ANNO`, and the dimension tools.
6. **Check** — `detect_rooms` with `wallLayers: ["A-WALL", "A-ROOM-SEP"]` and `labelLayers: ["A-ANNO"]` returns every room with its area, name and furniture.

Layers:

| Layer | Contents |
| --- | --- |
| `A-WALL` | Wall lines and jamb caps |
| `A-DOOR` | Door leaves and swings |
| `A-GLAZ` | Window glazing lines |
| `A-ROOM-SEP` | Room separation lines across openings; turn off for plotting |
| `A-FURN` | Furniture (`CB_BED`, `CB_SOFA`, ...) |
| `A-FIXT` | Fixtures and appliances (`CB_WC`, `CB_STOVE`, ...) |
| `A-ANNO` | Room names and notes |

Furniture symbols are defined once per drawing at a standard size in metres, centred on the block origin, and scaled to the `width` and `depth` you give. Room areas measured through a doorway include half the wall thickness under the opening, because the separation line runs along the wall centreline.

`insert_block` also reads `<name>.dwg` from `%LOCALAPPDATA%\AutoCADClaudeBridge\blocks` when the drawing has no block of that name, so an office block library can be dropped there. No other file path can be read.

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

Run the geometry regression tests (no AutoCAD needed):

```powershell
dotnet run -c Release --project .\tests\GeometryTests
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

The phased plan from drafting tools to AI floor-plan generation is in [ROADMAP.md](ROADMAP.md). Beyond it:

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
