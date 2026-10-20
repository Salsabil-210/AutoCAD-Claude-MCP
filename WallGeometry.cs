using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoCADClaudeBridge;

// Pure 2D geometry for drawing double-line walls with doors, windows and
// openings. Kept free of AutoCAD types so it can be tested outside AutoCAD.

// segment is the index of the path segment the opening sits on; offset is
// the distance from that segment's start point to the near edge of the
// opening, measured along the wall centreline.
internal sealed record WallOpening(
    int Segment,
    double Offset,
    double Width,
    string Kind,
    string Hinge,
    string Swing
);

internal enum PlanRole
{
    Wall,
    Door,
    Window,
    Separation
}

internal sealed record PlanLine(PlanRole Role, Vec Start, Vec End);

// Angles in radians; the arc runs counter-clockwise from start to end.
internal sealed record PlanArc(PlanRole Role, Vec Center, double Radius, double StartAngle, double EndAngle);

internal sealed record WallDrawing(List<PlanLine> Lines, List<PlanArc> Arcs);

internal static class WallGeometry
{
    internal const string Door = "door";
    internal const string Window = "window";
    internal const string Opening = "opening";
    private const double Epsilon = 1e-9;

    // Offsets the centreline path by half the thickness on each side, mitring
    // the corners. Doors and openings break both wall lines and get jamb caps
    // and a room separation line along the centreline, so detect_rooms keeps
    // the rooms on either side apart; doors also get a leaf and a 90 degree
    // swing arc. Windows keep the wall lines (the wall below the sill) and get
    // jamb lines and a glazing line, so room areas stop at the wall face.
    internal static WallDrawing Build(
        IReadOnlyList<Vec> path,
        bool closed,
        double thickness,
        IReadOnlyList<WallOpening> openings,
        bool capEnds
    )
    {
        var count = path.Count;
        if (count < (closed ? 3 : 2))
        {
            throw new ArgumentException(
                closed ? "A closed wall path needs at least 3 points." : "A wall path needs at least 2 points."
            );
        }

        var segments = closed ? count : count - 1;
        var directions = new Vec[segments];
        var lengths = new double[segments];
        for (var i = 0; i < segments; i++)
        {
            var delta = path[(i + 1) % count] - path[i];
            lengths[i] = delta.Length;
            if (lengths[i] <= thickness)
            {
                throw new ArgumentException($"Wall segment {i} must be longer than the wall thickness.");
            }
            directions[i] = delta * (1 / lengths[i]);
        }

        var half = thickness / 2;
        var lines = new List<PlanLine>();
        var arcs = new List<PlanArc>();
        var bySegment = openings
            .GroupBy(opening => opening.Segment)
            .ToDictionary(group => group.Key, group => group.OrderBy(opening => opening.Offset).ToList());
        foreach (var segment in bySegment.Keys)
        {
            if (segment < 0 || segment >= segments)
            {
                throw new ArgumentException($"Opening segment {segment} does not exist; the path has {segments} segments.");
            }
        }

        for (var i = 0; i < segments; i++)
        {
            var origin = path[i];
            var along = directions[i];
            var normal = Normal(along);
            var segmentOpenings = bySegment.TryGetValue(i, out var list) ? list : new List<WallOpening>();
            ValidateOpenings(segmentOpenings, i, lengths[i]);

            foreach (var side in new[] { half, -half })
            {
                var faceStart = FaceVertex(path, directions, closed, i, side);
                var faceEnd = FaceVertex(path, directions, closed, (i + 1) % count, side);
                var startAlong = Vec.Dot(faceStart - origin, along);
                var endAlong = Vec.Dot(faceEnd - origin, along);
                var cursor = faceStart;
                foreach (var opening in segmentOpenings)
                {
                    if (opening.Offset <= startAlong + Epsilon || opening.Offset + opening.Width >= endAlong - Epsilon)
                    {
                        throw new ArgumentException(
                            $"The {opening.Kind} on segment {i} reaches into a corner; move it away from the segment ends."
                        );
                    }
                    if (opening.Kind == Window)
                    {
                        continue;
                    }
                    AddLine(lines, PlanRole.Wall, cursor, origin + along * opening.Offset + normal * side);
                    cursor = origin + along * (opening.Offset + opening.Width) + normal * side;
                }
                AddLine(lines, PlanRole.Wall, cursor, faceEnd);
            }

            foreach (var opening in segmentOpenings)
            {
                var near = origin + along * opening.Offset;
                var far = origin + along * (opening.Offset + opening.Width);
                if (opening.Kind == Window)
                {
                    AddLine(lines, PlanRole.Window, near + normal * half, near - normal * half);
                    AddLine(lines, PlanRole.Window, far + normal * half, far - normal * half);
                    AddLine(lines, PlanRole.Window, near, far);
                    continue;
                }

                AddLine(lines, PlanRole.Wall, near + normal * half, near - normal * half);
                AddLine(lines, PlanRole.Wall, far + normal * half, far - normal * half);
                AddLine(lines, PlanRole.Separation, near, far);
                if (opening.Kind == Door)
                {
                    AddDoor(lines, arcs, origin, along, normal, half, opening);
                }
            }
        }

        if (capEnds && !closed)
        {
            foreach (var vertex in new[] { 0, count - 1 })
            {
                AddLine(
                    lines,
                    PlanRole.Wall,
                    FaceVertex(path, directions, closed, vertex, half),
                    FaceVertex(path, directions, closed, vertex, -half)
                );
            }
        }

        return new WallDrawing(lines, arcs);
    }

    private static void ValidateOpenings(List<WallOpening> openings, int segment, double length)
    {
        for (var k = 0; k < openings.Count; k++)
        {
            var opening = openings[k];
            if (opening.Offset < 0 || opening.Offset + opening.Width > length)
            {
                throw new ArgumentException($"The {opening.Kind} on segment {segment} extends past the segment.");
            }
            if (k > 0 && openings[k - 1].Offset + openings[k - 1].Width > opening.Offset)
            {
                throw new ArgumentException($"Openings on segment {segment} overlap.");
            }
        }
    }

    // The hinge sits on the jamb at the hinge end, on the face the door
    // swings towards; the leaf is drawn open at 90 degrees.
    private static void AddDoor(
        List<PlanLine> lines,
        List<PlanArc> arcs,
        Vec origin,
        Vec along,
        Vec normal,
        double half,
        WallOpening opening
    )
    {
        var side = opening.Swing == "left" ? 1 : -1;
        var hingeAt = opening.Hinge == "start" ? opening.Offset : opening.Offset + opening.Width;
        var closedAt = opening.Hinge == "start" ? opening.Offset + opening.Width : opening.Offset;
        var hinge = origin + along * hingeAt + normal * (side * half);
        var leafTip = hinge + normal * (side * opening.Width);
        var closedTip = origin + along * closedAt + normal * (side * half);
        AddLine(lines, PlanRole.Door, hinge, leafTip);

        var open = Angle(leafTip - hinge);
        var shut = Angle(closedTip - hinge);
        var sweep = NormalizeAngle(shut - open);
        arcs.Add(Math.Abs(sweep - Math.PI / 2) < 1e-6
            ? new PlanArc(PlanRole.Door, hinge, opening.Width, open, shut)
            : new PlanArc(PlanRole.Door, hinge, opening.Width, shut, open));
    }

    // Corner point of one wall face: the end of the path offset sideways, or
    // where the offset lines of the two segments meeting at the vertex cross.
    private static Vec FaceVertex(IReadOnlyList<Vec> path, Vec[] directions, bool closed, int vertex, double side)
    {
        var point = path[vertex];
        var segments = directions.Length;
        if (!closed && vertex == 0)
        {
            return point + Normal(directions[0]) * side;
        }
        if (!closed && vertex == path.Count - 1)
        {
            return point + Normal(directions[segments - 1]) * side;
        }

        var incoming = directions[(vertex - 1 + segments) % segments];
        var outgoing = directions[vertex % segments];
        var first = point + Normal(incoming) * side;
        var second = point + Normal(outgoing) * side;
        var cross = Vec.Cross(incoming, outgoing);
        if (Math.Abs(cross) < 1e-9)
        {
            if (Vec.Dot(incoming, outgoing) < 0)
            {
                throw new ArgumentException($"The wall path turns back on itself at point {vertex}.");
            }
            return first;
        }

        return first + incoming * (Vec.Cross(second - first, outgoing) / cross);
    }

    private static Vec Normal(Vec direction) => new(-direction.Y, direction.X);

    private static double Angle(Vec vector) => Math.Atan2(vector.Y, vector.X);

    private static double NormalizeAngle(double angle)
    {
        var turn = 2 * Math.PI;
        return ((angle % turn) + turn) % turn;
    }

    private static void AddLine(List<PlanLine> lines, PlanRole role, Vec start, Vec end)
    {
        if ((end - start).Length > Epsilon)
        {
            lines.Add(new PlanLine(role, start, end));
        }
    }
}
