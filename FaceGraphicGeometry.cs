using System;
using System.Collections.Generic;

namespace AutoCADClaudeBridge;

// Pure 2D geometry for graphics placed on a band face in elevation. Kept free
// of AutoCAD types so it can be tested outside AutoCAD.
//
// Coordinates are local to the elevation: x runs from the face's left end
// (0) to its right end (length), y from the band's bottom edge (0) to its top
// edge (height).

// One chevron ">" as a closed hexagon. Back is the x of its back edge's
// outer corners, Tip the x of its point.
internal sealed record Chevron(List<Vec> Outline, double Back, double Tip);

internal sealed record ChevronRow(List<Chevron> Chevrons, double Start, double End, double Bottom, double Top);

internal static class FaceGraphicGeometry
{
    internal const int MaxChevrons = 20;
    private const double Epsilon = 1e-9;

    // count chevrons, `pitch` apart, the first one's left-most point `at` from
    // the face's left end. Each is `stroke` wide measured along x, its point
    // `depth` ahead of its back corners, `chevronHeight` tall and centred on
    // the band height (so it may stand proud of the band top and bottom).
    // unitScale converts lengths in error messages back to the caller's units.
    internal static ChevronRow Chevrons(
        double faceLength,
        double bandHeight,
        double at,
        int count,
        double stroke,
        double pitch,
        double depth,
        double chevronHeight,
        bool pointRight,
        double unitScale = 1
    )
    {
        if (count < 1 || count > MaxChevrons)
        {
            throw new ArgumentException($"count must be 1 to {MaxChevrons}.");
        }
        if (stroke <= Epsilon || depth < 0 || chevronHeight <= Epsilon)
        {
            throw new ArgumentException("stroke and chevronHeight must be greater than zero, depth zero or more.");
        }
        if (count > 1 && pitch < stroke - Epsilon)
        {
            throw new ArgumentException("pitch must be at least the stroke, or the chevrons overlap.");
        }

        var width = (count - 1) * pitch + stroke + depth;
        if (at < -Epsilon || at + width > faceLength + Epsilon)
        {
            throw new ArgumentException(
                $"The chevrons run from {BandGeometry.Format(at, unitScale)} to {BandGeometry.Format(at + width, unitScale)}, outside the face (0 to {BandGeometry.Format(faceLength, unitScale)})."
            );
        }

        var middle = bandHeight / 2;
        var half = chevronHeight / 2;
        var chevrons = new List<Chevron>(count);
        for (var index = 0; index < count; index++)
        {
            // Built pointing right from x0, then mirrored inside the row when pointing left.
            var x0 = at + index * pitch;
            var outline = new List<Vec>
            {
                new(x0, middle + half),
                new(x0 + stroke, middle + half),
                new(x0 + stroke + depth, middle),
                new(x0 + stroke, middle - half),
                new(x0, middle - half),
                new(x0 + depth, middle)
            };
            var back = x0;
            var tip = x0 + stroke + depth;
            if (!pointRight)
            {
                var mirror = 2 * at + width;
                outline = outline.ConvertAll(point => new Vec(mirror - point.X, point.Y));
                outline.Reverse();
                (back, tip) = (mirror - back, mirror - tip);
            }
            chevrons.Add(new Chevron(outline, back, tip));
        }

        return new ChevronRow(chevrons, at, at + width, middle - half, middle + half);
    }
}
