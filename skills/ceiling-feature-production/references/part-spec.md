# Part spec

One JSON document per job. Claude fills it from the render and the designer's
answers, shows it to the designer, and only then draws. Every length is in
`job.units`.

```json
{
  "specVersion": 1,
  "job": {
    "number": "435",
    "site": "435 Erie, 4216 Main St, PA",
    "title": "CHANDELIER",
    "date": "2026-07-07",
    "units": "cm"
  },
  "materials": [
    { "id": "M1", "name": "Painted aluminium", "ral": "TBD", "aci": 5 }
  ],
  "parts": [
    {
      "id": "P1",
      "type": "band",
      "name": "Decorative chandelier",
      "quantity": 1,
      "material": "M1",
      "points": [{ "x": 0, "y": 0 }, { "x": 127.0, "y": 27.7 }, { "x": 127.0, "y": 171.1 }, { "x": 0, "y": 143.4 }],
      "cornerRadius": 15,
      "thickness": 5,
      "height": 10,
      "lightStrip": null,
      "faceGraphics": [],
      "source": { "sheet": 3, "confirmed": false }
    }
  ],
  "assembly": [],
  "openQuestions": []
}
```

## Fields

### job

| Field | Meaning |
| --- | --- |
| `number` | Project number shown in the title block (e.g. `435`). |
| `site` | Site address line. |
| `title` | Sheet title, usually the feature name (`CHANDELIER`). |
| `date` | Issue date, ISO format. |
| `units` | Unit of every length in the spec. Trust-Lines production books are in `cm` unless the designer says otherwise. Passed to every tool call as `units`. |

### materials

`id`, `name`, `ral` (RAL code or `TBD`), `aci` (AutoCAD Color Index used for the
drawing fill). A `TBD` colour is allowed while drafting but must be listed in
`openQuestions` and flagged on the sheet.

### parts

Common to every part: `id` (`P1`, `P2`, ...), `type`, `name`, `quantity`,
`material`, and `source`:

| `source` field | Meaning |
| --- | --- |
| `sheet` | Production book sheet the numbers came from, when reading an existing book. |
| `render` | Which render shows the part best, when reading renders (e.g. `"1"` or a file name). |
| `estimated` | Every field whose value is an estimate from a render or a default from `standards.md`, not a designer's number: `["height", "cornerRadius", "faceGraphics[0].letterHeight"]`. Remove a field from it once the designer confirms the value. |
| `confirmed` | `true` once the designer has checked every number of the part. |

`check_production_book` warns while a part is unconfirmed or still lists
estimated fields, so an estimate can never reach an issued sheet unnoticed.

#### type `band` — drawn by `draw_band_part`

A closed ring of sheet material standing on edge.

| Field | Meaning | Tool argument |
| --- | --- | --- |
| `points` | Outer **sharp** corners, in order around the ring. Face A runs from `points[0]` to `points[1]`, then B, C, ... Start at the front-left corner and go counter-clockwise so A is the front face. Convex outlines only. | `points` |
| `cornerRadius` / `cornerRadii` | Outer fillet radius, one value or one per corner. Inner radius = outer − thickness. | same |
| `thickness` | Sheet thickness, measured inward. | `thickness` |
| `height` | Band height seen in elevation. | `height` |
| `lightStrip` | `{ "height": 3, "offset": 8.5 }` — LED strip on every face, `offset` from the bottom edge (omit to centre). `null` for no light. | `lightStrip` |
| `groupFaces` | `false` when faces of equal length carry different graphics, so each gets its own elevation. Default `true`. | `groupFaces` |
| `faceGraphics` | Graphics on faces, one entry per face and graphic, drawn with `add_face_graphic` on that face's elevation. See below. | — |

Face graphic entries (every field except `face` is an `add_face_graphic` argument):

| `kind` | Fields |
| --- | --- |
| `letters` | `face`, `text`, `letterHeight`, `at` (from the face's left end; omit to centre), `finish` (`cut_through`, `engraved`, `applied`) |
| `chevrons` | `face`, `at` (face left end to the row's left-most point), `count`, `stroke` (width along the face), `pitch` (start to start), `depth` (how far the point leads), `chevronHeight` (centred on the band; more than the band height stands proud), `pointing` (`right`, `left`) |

The face's left end is the left end of its elevation as drawn: looking at the
face from outside the ring, with the band upright.

#### type `bent_bar` — drawn by `draw_bent_bar_part`

A strip of sheet on edge following a path in plan, such as the decorative arrow.

| Field | Meaning |
| --- | --- |
| `start`, `startAngle` | Where the centreline starts and the direction of the first step (degrees, 0 = +X). |
| `segments` | `{ "kind": "line", "length": 44.3 }` or `{ "kind": "arc", "radius": 15, "turn": "right", "angle": 105 }`; an arc may give `length` (along the centreline) instead of `angle`. Arcs are tangent to the run before them. |
| `thickness`, `height` | Strip thickness in plan and height in elevation. |
| `ends`, `arrowHeight` | `{ "start": "plain", "end": "arrow" }`; `arrowHeight` is the arrowhead's overall height, more than 2.414 × `height`. |

The developed (cut) length is the sum of the runs and arc lengths along the
centreline. A sheet that shows a shorter elevation is showing a projection;
note it in `openQuestions`.

### assembly — drawn by `draw_assembly_plan`

Where each part hangs, as an offset added to its own coordinates:
`{ "part": "P1", "at": { "x": 16.2, "y": 15.5 } }`. Hanger points go in a
top-level `hangers` array of `{ "x": ..., "y": ..., "drop": 120 }` in assembly
coordinates (`drop` = cable length to the ceiling). `check_production_book`
warns while `assembly` is empty.

### openQuestions

Plain sentences, one per thing the designer must still decide or confirm. A
spec with open questions can be drawn as a draft but never issued.
