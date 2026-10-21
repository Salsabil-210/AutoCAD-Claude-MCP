using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AutoCADClaudeBridge;

// Pure 2D geometry for ceiling band parts: a closed band of sheet material
// standing on edge, seen in plan as a ring with filleted corners. Kept free of
// AutoCAD types so it can be tested outside AutoCAD.
//
// The plan outline is given by its outer sharp corners (the corners the
// filleted outline would have without fillets) and an outer radius per
// corner. The band is `thickness` thick, measured inward; the inner outline
// keeps the same fillet centres, so its radius is the outer radius minus the
// thickness, or a sharp corner when the outer radius is smaller.

// A closed polyline: vertex i runs to vertex i + 1 as an arc when Bulges[i] is
// non-zero (bulge = tan(sweep / 4), positive counter-clockwise).
internal sealed record BandLoop(List<Vec> Vertices, List<double> Bulges, double Length);

// Face i runs from outer sharp corner i to corner i + 1 and is labelled
// A, B, C, ... in that order. EdgeLength is corner to corner; StraightLength
// is the flat part between the two fillets, and FlatMidpoint the middle of
// that flat part on the outer face. Outward points away from the inside of
// the ring.
internal sealed record BandFace(
    string Label,
    Vec Start,
    Vec End,
    double EdgeLength,
    double StraightLength,
    Vec Midpoint,
    Vec FlatMidpoint,
    Vec Outward
);

// One corner: its outer sharp point, the outer and inner fillet radii, and the
// unit vector pointing out of the ring along the corner bisector.
internal sealed record BandCorner(Vec Point, double OuterRadius, double InnerRadius, Vec Outward);

internal sealed record BandPart(
    BandLoop Outer,
    BandLoop Inner,
    BandLoop Centre,
    List<BandFace> Faces,
    List<BandCorner> Corners,
    double Thickness
);

// Faces with the same edge length are drawn as one elevation.
internal sealed record BandElevation(List<string> Labels, double Length, double StraightLength);

internal static class BandGeometry
{
    internal const int MaxFaces = 26;
    private const double Epsilon = 1e-9;
    private const double DuplicateTolerance = 1e-7;

    // unitScale converts the lengths in error messages back to the caller's
    // units (drawing units per caller unit).
    internal static BandPart Build(IReadOnlyList<Vec> corners, IReadOnlyList<double> radii, double thickness, double unitScale = 1)
    {
        var count = corners.Count;
        if (count < 3 || count > MaxFaces)
        {
            throw new ArgumentException($"A band part needs 3 to {MaxFaces} corners.");
        }
        if (radii.Count != count)
        {
            throw new ArgumentException($"Give one corner radius, or exactly {count} (one per corner).");
        }
        if (thickness <= Epsilon)
        {
            throw new ArgumentException("thickness must be greater than zero.");
        }
        if (radii.Any(radius => radius < 0 || !double.IsFinite(radius)))
        {
            throw new ArgumentException("Corner radii must be zero or positive.");
        }

        var area = 0.0;
        for (var index = 0; index < count; index++)
        {
            area += Vec.Cross(corners[index], corners[(index + 1) % count]);
        }
        if (Math.Abs(area) <= Epsilon)
        {
            throw new ArgumentException("The corners must enclose an area.");
        }
        // +1 when the corners run counter-clockwise; the left normal of each
        // edge then points into the ring.
        var turn = Math.Sign(area);

        var directions = new Vec[count];
        var lengths = new double[count];
        for (var index = 0; index < count; index++)
        {
            var edge = corners[(index + 1) % count] - corners[index];
            lengths[index] = edge.Length;
            if (lengths[index] <= Epsilon)
            {
                throw new ArgumentException($"Corners {index} and {(index + 1) % count} are the same point.");
            }
            directions[index] = edge * (1 / lengths[index]);
        }

        // Exterior turning angle at each corner. Only convex outlines are
        // supported: every corner must turn the same way.
        var turning = new double[count];
        for (var index = 0; index < count; index++)
        {
            var incoming = directions[(index + count - 1) % count];
            var outgoing = directions[index];
            var cross = Vec.Cross(incoming, outgoing) * turn;
            if (cross <= 1e-6)
            {
                throw new ArgumentException(
                    $"Corner {index} is not convex (or its edges are in line). Band parts must be convex outlines."
                );
            }
            turning[index] = Math.Atan2(cross, Vec.Dot(incoming, outgoing));
        }
        // A convex outline turns exactly once; a star turns the same way at
        // every corner but more than once.
        if (Math.Abs(turning.Sum() - 2 * Math.PI) > 1e-6)
        {
            throw new ArgumentException("The outline crosses itself. Band parts must be simple convex outlines.");
        }

        var outer = Offset(corners, radii, directions, lengths, turning, turn, 0, "outer", unitScale);
        var inner = Offset(corners, radii, directions, lengths, turning, turn, thickness, "inner", unitScale);
        var centre = Offset(corners, radii, directions, lengths, turning, turn, thickness / 2, "centre", unitScale);

        var faces = new List<BandFace>(count);
        for (var index = 0; index < count; index++)
        {
            var next = (index + 1) % count;
            var startTangent = TangentDistance(radii[index], turning[index]);
            var straight = lengths[index] - startTangent - TangentDistance(radii[next], turning[next]);
            var inward = new Vec(-directions[index].Y, directions[index].X) * turn;
            faces.Add(new BandFace(
                ((char)('A' + index)).ToString(),
                corners[index],
                corners[next],
                lengths[index],
                straight,
                (corners[index] + corners[next]) * 0.5,
                corners[index] + directions[index] * (startTangent + straight / 2),
                inward * -1
            ));
        }

        var bandCorners = new List<BandCorner>(count);
        for (var index = 0; index < count; index++)
        {
            var incoming = directions[(index + count - 1) % count];
            var outgoing = directions[index];
            var bisector = outgoing - incoming;
            var outward = bisector * (-1 / bisector.Length);
            bandCorners.Add(new BandCorner(corners[index], radii[index], Math.Max(radii[index] - thickness, 0), outward));
        }

        return new BandPart(outer, inner, centre, faces, bandCorners, thickness);
    }

    // Groups faces whose edge lengths agree within tolerance, in face order,
    // so identical faces share one elevation (like "B,C,D").
    internal static List<BandElevation> GroupFaces(IReadOnlyList<BandFace> faces, bool group, double tolerance)
    {
        var elevations = new List<BandElevation>();
        foreach (var face in faces)
        {
            var match = group
                ? elevations.FirstOrDefault(elevation =>
                    Math.Abs(elevation.Length - face.EdgeLength) <= tolerance
                    && Math.Abs(elevation.StraightLength - face.StraightLength) <= tolerance)
                : null;
            if (match is null)
            {
                elevations.Add(new BandElevation(new List<string> { face.Label }, face.EdgeLength, face.StraightLength));
            }
            else
            {
                match.Labels.Add(face.Label);
            }
        }
        return elevations;
    }

    internal static string Format(double value, double unitScale) =>
        (value / unitScale).ToString("0.###", CultureInfo.InvariantCulture);

    private static double TangentDistance(double radius, double turning) => radius * Math.Tan(turning / 2);

    // The outline `inset` inside the outer outline: each edge moves inward by
    // inset, the fillets keep their centres and lose inset from their radius.
    private static BandLoop Offset(
        IReadOnlyList<Vec> corners,
        IReadOnlyList<double> radii,
        Vec[] directions,
        double[] lengths,
        double[] turning,
        int turn,
        double inset,
        string name,
        double unitScale
    )
    {
        var count = corners.Count;
        var sharp = new Vec[count];
        var radius = new double[count];
        for (var index = 0; index < count; index++)
        {
            var incoming = directions[(index + count - 1) % count];
            var outgoing = directions[index];
            var inwardBisector = outgoing - incoming;
            inwardBisector *= 1 / inwardBisector.Length;
            // The interior half-angle is (pi - turning) / 2.
            var halfInterior = (Math.PI - turning[index]) / 2;
            sharp[index] = corners[index] + inwardBisector * (inset / Math.Sin(halfInterior));
            radius[index] = Math.Max(radii[index] - inset, 0);
        }

        var vertices = new List<Vec>();
        var bulges = new List<double>();
        // Where fillets meet with no straight between them, the next fillet
        // starts at the point the previous one ended: keep one vertex.
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
        var length = 0.0;
        for (var index = 0; index < count; index++)
        {
            var next = (index + 1) % count;
            var edgeLength = Vec.Dot(sharp[next] - sharp[index], directions[index]);
            var straight = edgeLength
                - TangentDistance(radius[index], turning[index])
                - TangentDistance(radius[next], turning[next]);
            if (straight < -1e-6)
            {
                throw new ArgumentException(
                    name == "outer"
                        ? $"The corner radii at both ends of face {(char)('A' + index)} do not fit on its {Format(lengths[index], unitScale)} length."
                        : $"The band is too thick for face {(char)('A' + index)}: its {name} outline has no straight part left."
                );
            }
            length += Math.Max(straight, 0);
        }

        for (var index = 0; index < count; index++)
        {
            var incoming = directions[(index + count - 1) % count];
            var outgoing = directions[index];
            if (radius[index] <= Epsilon)
            {
                Push(sharp[index], 0);
                continue;
            }
            var tangent = TangentDistance(radius[index], turning[index]);
            Push(sharp[index] - incoming * tangent, turn * Math.Tan(turning[index] / 4));
            Push(sharp[index] + outgoing * tangent, 0);
            length += radius[index] * turning[index];
        }
        if (vertices.Count > 1 && (vertices[^1] - vertices[0]).Length <= DuplicateTolerance)
        {
            // The last fillet ends where the first one starts; its bulge is 0.
            vertices.RemoveAt(vertices.Count - 1);
            bulges.RemoveAt(bulges.Count - 1);
        }

        return new BandLoop(vertices, bulges, length);
    }
}
