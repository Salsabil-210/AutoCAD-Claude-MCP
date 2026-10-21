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

From a render, list every physical part you can see. For each one decide its
type:

| What you see | Part type | Tool |
| --- | --- | --- |
| Closed ring or frame of sheet on edge (any convex outline, filleted corners) | `band` | `draw_band_part` |
| Band with a glowing line along it | `band` with `lightStrip` | `draw_band_part` |
| Flat bar bent in plan: arrow, swoosh, line | `bent_bar` | phase 2 — draw with `draw_polyline` / `draw_arc` for now |
| Letters, chevrons, logos on a band face | `faceGraphics` on the band | phase 2 — `add_text` / `draw_polyline` for now |

A render never gives exact sizes. Estimate proportions from it, but take real
dimensions only from the designer or from an existing drawing. Read
`examples/435-chandelier.json` first: it is a complete spec for a real job and
shows the expected level of detail, including the open questions.

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
- Typical sizes seen so far (job 435, cm): band thickness 5; plain band height
  10; lit band height 20 with a 3 high LED strip 8.5 from the bottom; corner
  radius R15 outer; bent-bar arrow 3 × 3. Use them only as defaults to propose,
  never as facts.
- Put every assumption and every unknown in `openQuestions`.

## 3. Confirm with the designer

Show the spec as a short table per part (name, type, outline, faces with
lengths, radius, thickness, height, light, graphics, quantity, material) and
the open questions. Ask the designer to confirm or correct it. Do not draw an
issued sheet from unconfirmed numbers; a draft is fine if they ask for one, and
it must be titled `DRAFT`.

## 4. Draw

1. `autocad_status` — check the drawing units. If unitless, ask what one unit
   is.
2. For each `band` part call `draw_band_part` with the spec fields. Place the
   next part to the right of the previous one: use the returned
   `extents.max.x` plus a gap of about a quarter of the part size.
3. Check the returned `faces` lengths against the spec. A mismatch means the
   spec is wrong: stop and tell the designer.
4. Phase 2 parts: draw them with the general tools and say they are not yet
   parametric.
5. `zoom_extents`.

Standard layers: `SIGN-BAND` (outlines, fills), `SIGN-LIGHT` (LED strip),
`SIGN-DIM` (dimensions), `SIGN-ANNO` (titles, face letters, notes). Fill colours
use the material `aci`.

## 5. Check before handing over

Report, per part:

- Every face of every band has an elevation (grouped faces count).
- Elevation lengths equal plan face lengths (the tool guarantees this; check
  hand-drawn parts yourself).
- Thickness is the same on plan and elevation.
- Radii: outer − thickness = inner on every corner.
- No colour is `TBD` on an issued sheet.
- Units are stated in the title block or notes.
- Hangers: position and drop length are given (not drawn by tools yet — list
  them as open if missing).
- Quantity is on every part.

Then list the open questions again. Never claim a sheet is ready while any
remain.

## Title block

Trust-Lines sheets carry: project number (large, left), site address, sheet
title (feature name), date, and three empty sign-off boxes — Üretim
Koordinatörü (production coordinator), Üretim Sorumlusu (production
supervisor), Proje Yöneticisi (project manager) — and the Trust-Lines Design and
Supply logo. The sheet frame and title block are a phase 3 tool; until then
note the title-block fields in the summary to the user.
