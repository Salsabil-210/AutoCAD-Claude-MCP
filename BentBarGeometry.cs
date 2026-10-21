using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoCADClaudeBridge;

// Pure 2D geometry for bent bar parts: a strip of sheet material standing on
// edge that follows a path of straight runs and arcs in plan (decorative
// arrows, swooshes). Kept free of AutoCAD types so it can be tested outside
// AutoCAD.
//
// The path is the strip's centreline. In plan the strip is `thickness` wide;
// in elevation it is unrolled flat into its developed length, `height` tall,
// with an optional arrowhead cut at either end.

// One step of the path. A line has Length; an arc has Radius and a signed
// Sweep in radians (positive = turning left, counter-clockwise).
internal sealed record BarStep(string Kind, double Length, double Radius, double Sweep);

// One piece of the built path, with where it starts along the developed length.
internal sealed record BarPiece(
    string Kind,
    Vec Start,
    Vec End,
    Vec StartDirection,
    Vec EndDirection,
    Vec Center,
    double Radius,
    double Sweep,
    double StartDistance,
    double Length
);

internal sealed record BentBar(List<BarPiece> Pieces, BandLoop Outline, double Length, Vec Min, Vec Max);

// The unrolled strip in elevation: shaft from x = 0 to Length with its bottom
// edge on y = 0, plus arrowheads. Min/Max include the arrowheads.
internal sealed record BarElevation(BandLoop Outline, double Length, Vec Min, Vec Max);

internal static class BentBarGeometry
{
    internal const string Line = "line";
    internal const string Arc = "arc";
    internal const string Plain = "plain";
    internal const string Arrow = "arrow";
    internal const int MaxSteps = 50;
    private const double Epsilon = 1e-9;
    private const double DuplicateTolerance = 1e-7;
    private const int ArcSamples = 32;

    internal static BentBar Build(Vec start, double startAngle, IReadOnlyList<BarStep> steps, double thickness)
    {
        if (steps.Count < 1 || steps.Count > MaxSteps)
        {
            throw new ArgumentException($"A bent bar needs 1 to {MaxSteps} path steps.");
        }
        if (thickness <= Epsilon)
        {
            throw new ArgumentException("thickness must be greater than zero.");
        }

        var pieces = new List<BarPiece>(steps.Count);
        var position = start;
        var direction = new Vec(Math.Cos(startAngle), Math.Sin(startAngle));
        var distance = 0.0;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step.Kind == Line)
            {
                if (step.Length <= Epsilon)
                {
                    throw new ArgumentException($"Step {index}: a line needs a length greater than zero.");
                }
                var end = position + direction * step.Length;
                pieces.Add(new BarPiece(Line, position, end, direction, direction, default, 0, 0, distance, step.Length));
                position = end;
                distance += step.Length;
                continue;
            }

            if (step.Kind != Arc)
            {
                throw new ArgumentException($"Step {index}: kind must be line or arc.");
            }
            if (Math.Abs(step.Sweep) <= Epsilon || Math.Abs(step.Sweep) >= 2 * Math.PI)
            {
                throw new ArgumentException($"Step {index}: an arc must turn between 0 and 360 degrees.");
            }
            if (step.Radius - thickness / 2 <= Epsilon)
            {
                throw new ArgumentException(
                    $"Step {index}: the arc radius must be more than half the thickness, or the inside of the bend folds over itself."
                );
            }
            var left = new Vec(-direction.Y, direction.X);
            var centre = position + left * (step.Radius * Math.Sign(step.Sweep));
            var arcEnd = centre + Rotate(position - centre, step.Sweep);
            var endDirection = Rotate(direction, step.Sweep);
            var length = step.Radius * Math.Abs(step.Sweep);
            pieces.Add(new BarPiece(Arc, position, arcEnd, direction, endDirection, centre, step.Radius, step.Sweep, distance, length));
            position = arcEnd;
            direction = endDirection;
            distance += length;
        }

        var outline = Outline(pieces, thickness);
        var (min, max) = Bounds(outline);
        return new BentBar(pieces, outline, distance, min, max);
    }

    // The developed strip in elevation. An arrowhead is two arms at 45
    // degrees, each as wide as the strip is tall, whose outer edges meet at
    // the tip and span arrowHeight from top to bottom.
    // unitScale converts lengths in error messages back to the caller's units.
    internal static BarElevation Elevation(double length, double height, string startEnd, string endEnd, double arrowHeight, double unitScale = 1)
    {
        if (length <= Epsilon || height <= Epsilon)
        {
            throw new ArgumentException("The developed length and the height must be greater than zero.");
        }
        var arrows = new[] { startEnd, endEnd }.Count(end => end == Arrow);
        if (arrows > 0 && arrowHeight < height * (1 + Math.Sqrt(2)) + Epsilon)
        {
            throw new ArgumentException(
                $"arrowHeight must be more than {BandGeometry.Format(height * (1 + Math.Sqrt(2)), unitScale)} (the height x 2.414) so the arrow arms clear the shaft."
            );
        }
        var reach = arrowHeight / 2 + height / Math.Sqrt(2);
        if (arrows * reach >= length)
        {
            throw new ArgumentException(
                $"The bar is too short for its arrowhead(s): each one reaches {BandGeometry.Format(reach, unitScale)} back from its tip."
            );
        }

        var right = EndPoints(length, height, endEnd, arrowHeight);
        var left = EndPoints(length, height, startEnd, arrowHeight)
            .Select(point => new Vec(length - point.X, point.Y))
            .Reverse();
        var vertices = right.Concat(left)
            .Select(point => new Vec(point.X, point.Y + height / 2))
            .ToList();
        var outline = new BandLoop(vertices, vertices.Select(_ => 0.0).ToList(), PolygonLength(vertices));
        var extent = Math.Max(height, arrows > 0 ? arrowHeight : height);
        return new BarElevation(outline, length, new Vec(0, height / 2 - extent / 2), new Vec(length, height / 2 + extent / 2));
    }

    // The right-hand end of a shaft centred on y = 0 and ending at x = length,
    // from its bottom edge round to its top edge, counter-clockwise.
    private static List<Vec> EndPoints(double length, double height, string end, double arrowHeight)
    {
        if (end != Arrow)
        {
            return new List<Vec> { new(length, -height / 2), new(length, height / 2) };
        }
        var half = arrowHeight / 2;
        var inset = height / Math.Sqrt(2);
        var join = length - height * (Math.Sqrt(2) + 0.5);
        return new List<Vec>
        {
            new(join, -height / 2),
            new(length - half - inset, -half + inset),
            new(length - half, -half),
            new(length, 0),
            new(length - half, half),
            new(length - half - inset, half - inset),
            new(join, height / 2)
        };
    }

    // Closed outline of the strip in plan: the left side forward, the end
    // cap, the right side back, the start cap.
    private static BandLoop Outline(List<BarPiece> pieces, double thickness)
    {
        var half = thickness / 2;
        var vertices = new List<Vec>();
        var bulges = new List<double>();
        void Push(Vec vertex, double bulge)
        {
            if (vertices.Count > 0 && (vertex - vertices[^1]).Length <= DuplicateTolerance)
            {
                bulges[^1] = bulge;
                return;
            }
            vertices.Add(vertex);
            bulges.Add(bulge);
        }
        Vec Left(Vec point, Vec direction) => point + new Vec(-direction.Y, direction.X) * half;
        Vec Right(Vec point, Vec direction) => point - new Vec(-direction.Y, direction.X) * half;
        double Bulge(BarPiece piece) => piece.Kind == Arc ? Math.Tan(piece.Sweep / 4) : 0;

        foreach (var piece in pieces)
        {
            Push(Left(piece.Start, piece.StartDirection), Bulge(piece));
        }
        var last = pieces[^1];
        Push(Left(last.End, last.EndDirection), 0);
        Push(Right(last.End, last.EndDirection), -Bulge(last));
        for (var index = pieces.Count - 1; index >= 0; index--)
        {
            var piece = pieces[index];
            Push(Right(piece.Start, piece.StartDirection), index > 0 ? -Bulge(pieces[index - 1]) : 0);
        }

        return new BandLoop(vertices, bulges, LoopLength(vertices, bulges));
    }

    private static (Vec Min, Vec Max) Bounds(BandLoop loop)
    {
        var points = new List<Vec>();
        for (var index = 0; index < loop.Vertices.Count; index++)
        {
            var start = loop.Vertices[index];
            var end = loop.Vertices[(index + 1) % loop.Vertices.Count];
            points.Add(start);
            var bulge = loop.Bulges[index];
            if (Math.Abs(bulge) <= Epsilon)
            {
                continue;
            }
            // Sample the arc through its centre and sweep.
            var sweep = 4 * Math.Atan(bulge);
            var chord = end - start;
            var radius = chord.Length / (2 * Math.Sin(Math.Abs(sweep) / 2));
            var middle = (start + end) * 0.5;
            var toCentre = new Vec(-chord.Y, chord.X) * (1 / chord.Length);
            var centre = middle + toCentre * (Math.Sign(sweep) * radius * Math.Cos(Math.Abs(sweep) / 2));
            for (var sample = 1; sample < ArcSamples; sample++)
            {
                points.Add(centre + Rotate(start - centre, sweep * sample / ArcSamples));
            }
        }
        return (
            new Vec(points.Min(point => point.X), points.Min(point => point.Y)),
            new Vec(points.Max(point => point.X), points.Max(point => point.Y))
        );
    }

    private static double LoopLength(List<Vec> vertices, List<double> bulges)
    {
        var length = 0.0;
        for (var index = 0; index < vertices.Count; index++)
        {
            var chord = (vertices[(index + 1) % vertices.Count] - vertices[index]).Length;
            var sweep = Math.Abs(4 * Math.Atan(bulges[index]));
            length += sweep <= Epsilon ? chord : chord * sweep / (2 * Math.Sin(sweep / 2));
        }
        return length;
    }

    private static double PolygonLength(List<Vec> vertices) =>
        LoopLength(vertices, vertices.Select(_ => 0.0).ToList());

    private static Vec Rotate(Vec vector, double angle) => new(
        vector.X * Math.Cos(angle) - vector.Y * Math.Sin(angle),
        vector.X * Math.Sin(angle) + vector.Y * Math.Cos(angle)
    );
}
