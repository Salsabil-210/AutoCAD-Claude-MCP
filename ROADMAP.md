# Roadmap: from CAD tools to AI floor-plan generation

The bridge grows in phases. Each phase builds on data the previous one has
proven correct. Tools stay narrow, typed, validated and undoable; a
"generate a floor plan" request is Claude calling these small tools in
sequence and checking each result, not one large tool.

Status: ✅ available · 🚧 in progress · ⏳ planned

## Phase 1 — Validate the foundation

Goal: prove the bridge works reliably on real drawings.

| Item | Status |
| --- | --- |
| `detect_rooms` exact on a test apartment (areas, perimeters, columns, doorways, drafting gaps, open wall ends, merged rooms flagged `needs_review`) | ✅ |
| Units: lengths in any unit, areas reported in m² | ✅ |
| Regression tests for the geometry in `tests/` | 🚧 |
| Test on a real apartment DWG | ⏳ needs a sample drawing |
| Claude Desktop extension reinstalled so Claude calls the new tools directly | ⏳ after the Phase 2 build |

## Phase 2 — Drafting toolkit

Goal: Claude can draw a complete furnished plan like the reference image.

| Requested tool | Provided by | Status |
| --- | --- | --- |
| `create_line`, `create_polyline`, `create_rectangle` | `draw_line`, `draw_polyline`, `draw_rectangle` | ✅ |
| `delete_entities`, `move_entities` | `erase_entities`, `move_entities` | ✅ |
| `create_arc`, circles | `draw_arc`, `draw_circle` | 🚧 |
| `rotate_entities`, copy, mirror | `rotate_entities`, `copy_entities`, `mirror_entities` | 🚧 |
| `create_wall`, `create_wall_chain`, `create_wall_corner` | `draw_walls`: one centreline path, a thickness, mitred corners | 🚧 |
| `create_door`, `create_window`, `create_opening` | `openings` on `draw_walls`: door leaf and swing arc, glazing lines, jamb caps, room separation line | 🚧 |
| `insert_block` | `insert_block`: blocks already in the drawing or from the bridge block library folder | 🚧 |
| `place_furniture`, `place_bed`, `place_sofa`, `place_bathroom_fixture`, `place_appliance`, `place_kitchen_cabinet` | `place_furniture` with a `kind` (bed, sofa, wc, basin, bathtub, stove, fridge, ...) | 🚧 |
| `place_table_and_chairs` | `place_furniture` table and chair, then `copy_entities` / `rotate_entities` | 🚧 |
| `create_layer`, `assign_entity_layer` | Standard layers created by the tools: `A-WALL`, `A-DOOR`, `A-GLAZ`, `A-ROOM-SEP`, `A-FURN`, `A-FIXT`, `A-ANNO` | 🚧 |
| `add_dimension`, `add_leader`, `add_text` | `add_linear_dimension`, `add_aligned_dimension`, `add_angular_dimension`, `add_leader`, `add_text` | ✅ |

## Phase 3 — Trusted room data

Goal: rooms become stored, named records that later features rely on.

| Requested tool | Provided by | Status |
| --- | --- | --- |
| Room records saved in the DWG, stale detection, stable IDs | `save_rooms`, `get_rooms` | 🚧 separate session |
| `create_room_schedule` | `export_room_schedule` | 🚧 separate session |
| `assign_room_function` | room function on saved records | ⏳ |
| `place_room_label`, `add_area_label` | label at a point inside each room, area from the saved record | ⏳ |
| `create_room` | room from a polygon or dimensions, saved as a record | ⏳ |

## Phase 4 — Editing and layout helpers

| Requested tool | Plan | Status |
| --- | --- | --- |
| `offset_geometry`, `trim_geometry`, `extend_geometry`, `join_geometry` | general editing tools by handle | ⏳ |
| `connect_walls`, `modify_wall`, `create_opening` in an existing wall | edit walls drawn by `draw_walls` | ⏳ |
| `snap_to_wall`, `align_furniture`, `distribute_furniture` | placement helpers using room boundaries | ⏳ |
| `validate_wall_network` | gaps, overlaps and open ends (partly in `detect_rooms` today) | ⏳ |

## Phase 5 — Design checks

Read-only checks that return pass, fail or needs-review with handles. Rule
values (clearances, minimum sizes) come from a per-project rules file,
because codes differ by country.

`check_room_dimensions`, `check_door_clearance`, `check_corridor_width`,
`check_furniture_clearance`, `check_furniture_collisions`,
`check_accessibility`, `check_room_connectivity`, `check_plan_quality`,
`highlight_issues` (writes markers only after approval). ⏳

## Phase 6 — Orchestrated generation

`generate_floor_plan` as a documented workflow Claude follows with the
Phase 2–5 tools: understand the brief, plan the spaces, draw walls, add
openings, furnish, validate, annotate, and show a preview for approval.
Then `generate_layout_variants` on separate layers and
`optimize_room_layout` against Phase 5 checks. ⏳

## Phase 7 — Drawing output

`set_drawing_scale`, `set_lineweight`, `set_linetype`,
`annotate_doors_windows`, `set_block_attributes`, `export_drawing_pdf`,
`export_drawing_dwg`, `export_plan_image`. File-writing tools need the
same path restrictions as the block library. ⏳

## Ceiling features track — production books from renders

Goal: from a render of a suspended ceiling feature, Claude produces the
Trust-Lines production book (one sheet per part: plan, every face in
elevation, dimensions, legend, title block). Job 435 is the test case.

| Phase | Item | Status |
| --- | --- | --- |
| C1 | Part spec format and the `ceiling-feature-production` skill | ✅ |
| C1 | `draw_band_part`: rings, frames and lit bands with grouped elevations | ✅ |
| C1 | Rebuild 435 sheets 2 and 3 from the spec in AutoCAD | 🚧 needs a run in AutoCAD |
| C2 | `draw_bent_bar_part`: arrows and swooshes, developed length, arrow/chevron ends | ⏳ |
| C2 | `add_face_graphic`: cut letters, chevrons, logos on band elevations | ⏳ |
| C3 | `draw_sheet`: Trust-Lines frame and title block, material legend | ⏳ needs the title block DWG |
| C3 | `draw_assembly_plan`, `check_production_book`, `plot_sheets_pdf` | ⏳ |
| C4 | Render reading tested on new jobs | ⏳ needs more past books |
