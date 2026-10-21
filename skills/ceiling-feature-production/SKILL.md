---
name: ceiling-feature-production
description: Turn a render or concept image of a suspended ceiling feature (chandelier rings, frames, lit bands, bent arrows, chevrons, cut letters) into a Trust-Lines production book in AutoCAD through the AutoCAD Local Bridge. Use when the user shares a render, sketch or production book of a ceiling-hung sign or decorative piece and wants parts, dimensions, elevations or production drawings.
---

# Ceiling feature production books

Trust-Lines Design & Supply makes suspended ceiling features for retail
interiors: rings and frames hung from the ceiling, lit bands with LED strips,
bent flat-bar arrows and chevrons, and cut letters on band faces. A production
book has one sheet per part: plan, an elevation of every face, dimensions,
material legend and title block. The workshop builds from it, so every number
must be consistent and nothing may be guessed silently.

Work in this order. Do not skip the confirmation step.

## 1. Read the input

For a render, follow `references/reading-renders.md` step by step: inventory,
classify, plan shape, relationships, proportions, faces and graphics, colour
and light. Then compare with the closest past job in `examples/` (see
`examples/README.md`) and its render notes.

For each part decide its type:

| What you see | Part type | Tool |
| --- | --- | --- |
| Closed ring or frame of sheet on edge (any convex outline, filleted corners) | `band` | `draw_band_part` |
| Band with a glowing line along it | `band` with `lightStrip` | `draw_band_part` |
| Flat bar bent in plan: arrow, swoosh, line | `bent_bar` | `draw_bent_bar_part` |
| Letters or chevrons on a band face | `faceGraphics` on the band | `add_face_graphic` |
| Logo on a band face | `faceGraphics` on the band | not parametric yet — `insert_block` from the block library |

A render never gives exact sizes. Estimate proportions from it, but take real
dimensions only from the designer or from an existing drawing. Read
`examples/435-chandelier.json` first: it is a complete spec for a real job and
shows the expected level of detail, including the open questions. Leave out
render overlays (project number boxes) and neighbouring signs, and say so.

## 2. Write the part spec

Fill a part spec as described in `references/part-spec.md`. Rules:

- Units: Trust-Lines books are in **cm** unless the designer says otherwise.
  Set `job.units` and pass `units` on every bridge call with the numbers
  unchanged. Never convert units yourself.
- Band outline: `points` are the **outer sharp corners** (where the faces would
  meet without fillets). Start at the front-left corner and go
  counter-clockwise so face A is the front (viewer side) face, then B, C, D.
- Corner radius is the **outer** radius; the inner radius is outer − thickness
  (job 435: R15 outside, 5 thick, R10 inside).
- A face's elevation length is its **corner-to-corner** length. The flat part
  between fillets is reported separately; never draw an elevation at a length
  that differs from its plan face.
- Sizes the designer did not give: propose the defaults in
  `references/standards.md` (provisional until the office confirms them), or
  the closest past job's. Never present them as facts.
- List every estimated or defaulted field in the part's `source.estimated`,
  and set `source.confirmed: false`.
- Put every assumption and every unknown in `openQuestions`, each with your
  proposed answer so the designer can reply yes or no.

## 3. Check and confirm with the designer

Run `check_production_book` with the spec. Fix every **error** with the
designer before drawing (never by quietly changing their numbers). Then show
the spec as a short table per part (name, type, outline, faces with lengths,
radius, thickness, height, light, graphics, quantity, material), the checker's
warnings and the open questions, and ask the designer to confirm or correct it.
Run the checker again after every change.

`issuable: true` is the only state in which sheets may be drawn without
`draft: true`. A draft is fine if the designer asks for one.

## 4. Draw

1. `autocad_status` — check the drawing units. If unitless, ask what one unit
   is.
2. For each `band` part call `draw_band_part` with the spec fields. Set
   `groupFaces: false` when equal faces carry different graphics. Place the
   next part to the right of the previous one: use the returned
   `extents.max.x` plus a gap of about a quarter of the part size.
3. Check the returned `faces` lengths against the spec. A mismatch means the
   spec is wrong: stop and tell the designer.
4. For each of the band's `faceGraphics`, call `add_face_graphic` with the
   matching entry of the returned `elevations` (the one whose `faces` holds
   that face) passed unchanged as `elevation`, the returned `textHeight`, and
   the entry's fields. If it refuses (graphic off the face), tell the designer
   the numbers it reported; do not shrink or move the graphic yourself.
5. For each `bent_bar` part call `draw_bent_bar_part`. Check its
   `developedLength` against any length on the source sheet: a shorter sheet
   length is usually a projected view, so ask.
6. `draw_assembly_plan` with every part's geometry and its `assembly` position
   (`at`) and the hanger points, placed clear of the parts drawn so far.
7. `zoom_extents`.

Standard layers: `SIGN-BAND` (band and bar outlines, fills), `SIGN-LIGHT` (LED
strip), `SIGN-GRAPHIC` (letters, chevrons), `SIGN-DIM` (dimensions), `SIGN-ANNO`
(titles, face letters, bend lines, notes, callouts), `SIGN-SHEET` (frames, title
blocks). Fill colours use the material `aci`.

## 4b. Sheets and PDF

1. One `draw_sheet` per sheet, in book order: sheet 1 is the assembly plan, then
   one sheet per part in spec order. Pass each tool's returned `extents`, the
   job fields, `sheetName` (the part name, or `Plan`), `sheetNumber`,
   `sheetCount`, the materials the sheet shows (name, ral, aci) and
   `draft: true` unless `check_production_book` reports `issuable: true`.
   Draw each sheet around one part only: leave enough space between parts
   (step 4.2) that sheet frames do not overlap.
2. `list_sheets` — confirm every sheet is there once, in order. A sheet drawn
   twice by mistake: erase its handles and draw it again; skip the stale ID
   when plotting.
3. `plot_sheets_pdf` — ask the user where to save it, and ask before passing
   `overwrite: true`.

## 5. Check before handing over

Report, per part:

- Every face of every band has an elevation (grouped faces count).
- Elevation lengths equal plan face lengths (the tool guarantees this; check
  hand-drawn parts yourself).
- Thickness is the same on plan and elevation.
- Radii: outer − thickness = inner on every corner.
- No colour is `TBD` on an issued sheet.
- Units are stated in the title block or notes.
- Hangers: positions are on the assembly plan and drop lengths are given; list
  them as open if missing.
- Quantity is on every part.

Then list the open questions again. Never claim a sheet is ready while any
remain.

## 6. After the job is issued

Offer to add it to `examples/` (see `examples/README.md`): the final spec, its
render notes with what the render got wrong, and any change to
`references/standards.md`. Each job added makes the next render reading better.

## Title block

Trust-Lines sheets carry: project number (large, left), site address, sheet
title (feature name), date, and three empty sign-off boxes — Üretim
Koordinatörü (production coordinator), Üretim Sorumlusu (production
supervisor), Proje Yöneticisi (project manager) — and the Trust-Lines Design and
Supply logo. `draw_sheet` draws all of it; the logo comes from the
`TRUSTLINES_LOGO` block when the office has put it in the block library, and is a
text placeholder otherwise — say so when you hand over a book without it.
