# Reading a render into a draft spec

A render shows what the feature looks like, not how it is built. It is
perspective, usually seen from below at an angle, often with lighting and
background effects. Use it to decide **what parts exist, their shape and how
they relate**. Take **sizes** from the designer, a drawing, or
`standards.md`. Never let a render number pass as a measurement.

## What a render can and cannot give

| Can give (with care) | Cannot give |
| --- | --- |
| How many separate parts there are | Material thickness |
| Each part's type and plan shape (ring, frame, bent bar) | Exact corner radii |
| Number of faces and whether opposite faces are parallel | Band heights to better than a rough proportion |
| Which faces carry letters, chevrons, logos, and roughly where | Letter height, font, or cut-through vs engraved for sure |
| Which parts glow (LED), and where | LED product, strip size, wiring |
| Nesting and overlap between parts | Hanging heights, cable positions, drop lengths |
| Colour families (teal, cream, white) | RAL codes |
| The text content, as seen | Correct spelling: confirm it |

## Method

Work through the steps in order and write down what you see before you
interpret it.

### 1. Inventory

List every physical piece. Separate pieces show as a change of colour, a
visible gap or joint, a different edge thickness, or one passing in front of
another. Name each one with what it looks like ("teal outer ring", "cream
arrow").

Leave out what is not part of the job, and say so:

- presentation overlays: project number boxes ("PROJECT NO. 435"), logos in a
  corner, watermarks;
- the surroundings: ceiling, shelves, other signage. A sign hanging below the
  feature, such as "CHECKOUT", is a separate job unless the designer says it is
  part of this one.

### 2. Classify each piece

| What you see | Type |
| --- | --- |
| A continuous strip on edge that closes on itself; you see its thin top edge and a tall face | `band` |
| A band with a glowing line, or light washing out of it | `band` with `lightStrip` |
| A strip on edge with two free ends, bent in plan; often an arrow | `bent_bar` |
| Text on a band face; light or background seen through the letters means cut through | `faceGraphics` letters |
| Repeated `>` shapes on a face, often standing proud of the band | `faceGraphics` chevrons |
| A brand mark on a face | logo (not parametric yet) |
| Thin vertical lines to the ceiling | hangers |

If a piece fits none of these, say so and ask; do not force it into a type.

### 3. Plan shape

The plan is what you would see looking straight up. From the render:

- Count the corners.
- Opposite faces parallel? Faces at right angles, or skewed? Rings in retail
  features are often parallelograms: the long faces run parallel to an aisle
  while the short faces are skewed (job 435).
- Corners rounded or sharp?
- Is the outline convex? `draw_band_part` only draws convex outlines.

### 4. Relationships

- Nesting: which ring sits inside which, and is the gap even all round?
- Shared directions: do the inner and outer rings have the same skew? (In 435
  they do.)
- Contact: where does the arrow touch or cross a ring, and which part is in
  front?
- Heights: which parts hang higher or lower?

### 5. Proportions

Only with one real size from the designer (for example "the outer ring is
130 wide"):

- Scale other sizes from it only on the same face, or on faces at about the
  same depth and angle. Foreshortening makes faces that turn away from the
  viewer look shorter.
- Give an estimate as a rounded number and record it in `source.estimated`.
- Thickness, radii and heights always come from `standards.md` or the
  designer, never from scaling.

Without a real size, record proportions only ("inner ring about 3/4 of the
outer") and ask for one size.

### 6. Faces and graphics

- Face A is the front face: the one facing the main view (the aisle). Then go
  counter-clockwise in plan: B, C, D.
- For each graphic: which face, the exact text or shape count, roughly where
  along the face (as a fraction from its left end, seen from outside), height
  relative to the band, and whether it stands proud.
- Chevrons usually come in groups of two or three and point the walking
  direction. Note which way.

### 7. Colour and light

Describe colours in words ("teal", "cream"), and put them in `materials` with
`ral: "TBD"` and a suggested `aci`. Note which parts glow and where the light
comes from: a strip line on the face, a glow from the top or bottom edge, or
backlit letters.

## Output: the draft spec

Fill the spec (`part-spec.md`) with:

- every part found, each with `source.render` (which render shows it best) and
  `source.estimated` listing every field that is an estimate or a standard
  default rather than a designer's number;
- `source.confirmed: false` on every part;
- `openQuestions`: one specific question per unknown, each with your proposed
  answer, so the designer can reply "yes" (see below).

Then run `check_production_book` and fix what it reports before you show the
designer anything.

## Asking the designer

Group the questions by part. Make each one answerable in a word:

- Good: "Outer ring: band 10 high and 5 thick, R15 corners, like job 435?"
- Good: "The arrow points left in render 2. Is the arrowhead at the free end of
  the long leg?"
- Bad: "What are the dimensions?"

Always ask for: one overall size per part (if not given), thickness, band
heights, corner radii, letter height and how letters are made, LED product,
colours (RAL), quantity, hanger positions and drop lengths.

## Common traps

- Reading a render overlay (project number box) as a part.
- Treating the arrow's elevation length on a sheet as its cut length: the cut
  length is the developed length (435: 161.9, not 106.1).
- Taking the visible face height in a render from below as the band height:
  you see the face at an angle, so it looks shorter.
- Assuming opposite skewed faces are equal: check them, then group them.
- Missing a face graphic on a face turned away from the camera: ask about every
  face that cannot be seen.
