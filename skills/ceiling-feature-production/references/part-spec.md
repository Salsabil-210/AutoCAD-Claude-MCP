# Part spec

One JSON document per job. Claude fills it from the render and the designer's
answers, shows it to the designer, and only then draws. Every length is in
`job.units`. Fields marked *(phase 2)* or *(phase 3)* are recorded now but not
drawn yet.

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
`material`, `source` (`sheet` it came from, `confirmed` = the designer checked
the numbers).

#### type `band` — drawn by `draw_band_part`

A closed ring of sheet material standing on edge.

| Field | Meaning | Tool argument |
| --- | --- | --- |
| `points` | Outer **sharp** corners, in order around the ring. Face A runs from `points[0]` to `points[1]`, then B, C, ... Start at the front-left corner and go counter-clockwise so A is the front face. Convex outlines only. | `points` |
| `cornerRadius` / `cornerRadii` | Outer fillet radius, one value or one per corner. Inner radius = outer − thickness. | same |
| `thickness` | Sheet thickness, measured inward. | `thickness` |
| `height` | Band height seen in elevation. | `height` |
| `lightStrip` | `{ "height": 3, "offset": 8.5 }` — LED strip on every face, `offset` from the bottom edge (omit to centre). `null` for no light. | `lightStrip` |
| `faceGraphics` *(phase 2)* | Letters, chevrons or logos on a face: `{ "face": "A", "kind": "letters", "text": "COFFEE", "height": 8.5, "offset": 0, "cut": "through" }`. | — |

#### type `bent_bar` *(phase 2)*

A flat bar bent in plan, such as the decorative arrow: `path` (lines and arcs),
`width`, `thickness`, `ends` (`arrow`, `chevron`, `plain`).

### assembly *(phase 3)*

Where each part sits in the assembly plan: `{ "part": "P1", "at": { "x": 0, "y": 0 } }`,
plus hanger points.

### openQuestions

Plain sentences, one per thing the designer must still decide or confirm. A
spec with open questions can be drawn as a draft but never issued.
