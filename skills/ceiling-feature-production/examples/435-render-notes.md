# Job 435 — chandelier, render notes

Source: `435 PRODUCITON BOOK.pdf` (4 sheets, dated 07.07.2026). Sheet 1 holds
three small renders next to the plan. There were no separate render files, so
these notes read the renders against the finished book: what each render shows,
and what only the book could tell.

## What the renders show

| Render | View | Seen |
| --- | --- | --- |
| 1 (left) | Low angle, from the front-left | Teal band with white `>>` chevrons near its left end and white **COFFEE** letters; a cream arrow pointing **left** passing in front; a "PROJECT NO. 435" box (overlay); a separate CHECKOUT sign below. |
| 2 (middle) | From the front, lower | The cream arrow pointing left across the teal band; white `>>` chevrons further right; part of the CHECKOUT sign. |
| 3 (right) | From below, at an angle | Several nested teal rings with thin light lines along them: the lit ring and the outer decorative ring, seen from underneath. |

## How each part reads

| Part | From the renders | From the book only |
| --- | --- | --- |
| P1 Chandelier with light | Inner teal ring; glowing line along it; COFFEE on its front face. | Parallelogram 100 × 117.9; 20 high; 5 thick; R15/R10; LED strip 3 high, 8.5 from the bottom; letters about 10 high. |
| P2 Decorative chandelier | Outer teal ring; two white chevrons on several faces, standing proud of the band. | Parallelogram 130 × 143.4 at a 27.7 skew; 10 high; 5 thick; R15/R10; chevrons 16 high, 3 proud, at 25 / 95.4 / 82 on faces A / B / C. |
| P3 Decorative arrow | Cream strip, arrowhead at one end, running along and across the rings. | Bent in plan: 44.3 run, R15 bend of 27.5, 90.1 run; 3 × 3 strip; arrowhead 30.9; cut length 161.9. |

## What the renders could not tell

- Every size: thickness, heights, radii, LED strip, letter height.
- That the rings are parallelograms with the same skew: the renders show
  rounded rings, but not the skew angle.
- That the arrow is bent in plan around the ring corner: in the renders it
  reads as a straight arrow.
- Whether face D carries chevrons (turned away from every camera).
- Colours (RAL), materials, LED product, hangers.

## Traps this job hit

- **Overlay read as a part.** The "PROJECT NO. 435" box sits on the renders
  like a hanging tag. It is a presentation label.
- **Neighbouring sign.** CHECKOUT hangs below the feature in two renders but is
  not in the book.
- **Arrow length.** Sheet 4 elevation A shows 106.1, a projected view; the cut
  length is 161.9.
- **Elevation lengths copied between faces.** Sheet 2 draws faces B, C, D all
  at 100.0, but face D is 117.9 in plan. `draw_band_part` takes elevation
  lengths from the plan, so this cannot happen in drawings it makes.
- **Thickness given twice.** Sheet 4 gives the arrow 3.6 in plan and 3.0 in
  elevation.

## Lessons for the next job

- Ask for the plan outline (or one overall size and the skew) as the first
  question: a render from below cannot give it.
- Ask what is on every face the cameras cannot see.
- Treat any straight-looking strip that runs past a ring corner as possibly
  bent in plan, and ask.
