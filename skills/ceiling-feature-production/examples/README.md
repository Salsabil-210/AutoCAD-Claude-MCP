# Past jobs

Worked examples Claude compares a new render against. Each job is two files:

| File | What it holds |
| --- | --- |
| `<job>-<feature>.json` | The final part spec, as built: the numbers from the issued production book, with `source.confirmed: true` once the designer has checked them. |
| `<job>-render-notes.md` | What the renders showed, how each part was read, what the render could not tell, and what turned out different in the final book. |

The source files (renders, production book PDF, DWG) stay in the office's job
folders; the notes say where.

## Jobs

| Job | Feature | Parts | Spec | Notes | Status |
| --- | --- | --- | --- | --- | --- |
| 435 | Chandelier (Erie, PA) | lit ring with COFFEE, decorative ring with chevrons, bent arrow | [435-chandelier.json](435-chandelier.json) | [435-render-notes.md](435-render-notes.md) | spec read from the production book; designer has not confirmed it |

## Using the examples

1. Find the job with the most similar parts (same part types, similar size).
2. Reuse its construction: thicknesses, heights, radii, graphic sizes. Record
   each reused value in the new part's `source.estimated`, with the job number
   in an open question ("Band 10 high, 5 thick, R15 corners, as on job 435?").
3. Read its render notes for the traps that job hit.

## Adding a job

1. Copy the closest spec, rename it `<job>-<feature>.json`, and fill it from
   the issued production book. Run `check_production_book` on it: an issued
   job should have no errors.
2. Write `<job>-render-notes.md` with the sections used in
   `435-render-notes.md`.
3. Add a row to the table above.
4. Update `../references/standards.md`: add the job to "Seen in", and change a
   default when the office has moved on from it.

Three or more jobs make the standards trustworthy; until then treat every
default as a question for the designer.
