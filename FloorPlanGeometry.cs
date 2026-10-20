using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoCADClaudeBridge;

// Pure 2D geometry for floor plan analysis. Kept free of AutoCAD types so the
// room detection can be tested outside AutoCAD.
internal readonly record struct Vec(double X, double Y)
{
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec operator *(Vec a, double s) => new(a.X * s, a.Y * s);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public static double Cross(Vec a, Vec b) => a.X * b.Y - a.Y * b.X;
    public static double Dot(Vec a, Vec b) => a.X * b.X + a.Y * b.Y;
}

internal sealed record WallSegment(Vec Start, Vec End, string Source);

internal sealed record RoomFace(
    List<Vec> Boundary,
    List<List<Vec>> Holes,
    double Area,
    double NetArea,
    double Perimeter,
    Vec Centroid
);

internal sealed record WallGap(Vec Point, string Source);

// kind is "opening" for a doorway-sized gap closed along the wall line, or
// "drafting_gap" for a small gap where wall lines nearly meet.
internal sealed record ClosedGap(string Kind, Vec Start, Vec End, double Width);

internal sealed record RoomDetection(
    List<RoomFace> Rooms,
    List<WallGap> Gaps,
    List<ClosedGap> Closed
);

internal static class FloorPlanGeometry
{
    // A dangling wall end may be joined to another one up to maxOpeningWidth
    // away only when the join continues the wall line (a doorway); any other
    // direction is joined only within maxDraftingGap.
    private const double OpeningAlignmentDegrees = 15;

    internal static RoomDetection DetectRooms(
        IReadOnlyList<WallSegment> walls,
        double tolerance,
        double maxOpeningWidth,
        double maxDraftingGap,
        double minArea,
        double minWidth
    )
    {
        var graph = new PlanarGraph(tolerance);
        AddSplitSegments(graph, walls, tolerance);

        var closed = CloseGaps(graph, maxOpeningWidth, maxDraftingGap);
        var gaps = graph.Vertices
            .Select((_, index) => index)
            .Where(index => graph.Degree(index) == 1)
            .Select(index => new WallGap(graph.Vertices[index], graph.Sources[index]))
            .ToList();

        graph.PruneDanglingEdges();
        var faces = graph.TraceFaces();
        var rooms = BuildRooms(graph, faces, minArea, minWidth);
        return new RoomDetection(rooms, gaps, closed);
    }

    internal static bool Contains(RoomFace room, Vec point)
    {
        return InPolygon(room.Boundary, point) && !room.Holes.Any(hole => InPolygon(hole, point));
    }

    internal static bool InPolygon(IReadOnlyList<Vec> polygon, Vec point)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    internal static double SignedArea(IReadOnlyList<Vec> polygon)
    {
        var sum = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            sum += Vec.Cross(polygon[i], polygon[(i + 1) % polygon.Count]);
        }

        return sum / 2;
    }

    // Splits every wall at its crossings and T-junctions with other walls so
    // the plan becomes a planar graph.
    private static void AddSplitSegments(
        PlanarGraph graph,
        IReadOnlyList<WallSegment> walls,
        double tolerance
    )
    {
        var splits = walls.Select(_ => new List<double> { 0, 1 }).ToArray();
        for (var i = 0; i < walls.Count; i++)
        {
            for (var j = i + 1; j < walls.Count; j++)
            {
                Intersect(walls[i], walls[j], tolerance, splits[i], splits[j]);
            }
        }

        for (var i = 0; i < walls.Count; i++)
        {
            var wall = walls[i];
            var direction = wall.End - wall.Start;
            var previous = -1;
            foreach (var t in splits[i].Distinct().OrderBy(value => value))
            {
                var vertex = graph.AddVertex(wall.Start + direction * t, wall.Source);
                if (previous >= 0)
                {
                    graph.AddEdge(previous, vertex);
                }
                previous = vertex;
            }
        }
    }

    private static void Intersect(
        WallSegment first,
        WallSegment second,
        double tolerance,
        List<double> firstSplits,
        List<double> secondSplits
    )
    {
        if (Math.Max(first.Start.X, first.End.X) + tolerance < Math.Min(second.Start.X, second.End.X)
            || Math.Max(second.Start.X, second.End.X) + tolerance < Math.Min(first.Start.X, first.End.X)
            || Math.Max(first.Start.Y, first.End.Y) + tolerance < Math.Min(second.Start.Y, second.End.Y)
            || Math.Max(second.Start.Y, second.End.Y) + tolerance < Math.Min(first.Start.Y, first.End.Y))
        {
            return;
        }

        var d1 = first.End - first.Start;
        var d2 = second.End - second.Start;
        var length1 = d1.Length;
        var length2 = d2.Length;
        if (length1 <= tolerance || length2 <= tolerance)
        {
            return;
        }

        var offset = second.Start - first.Start;
        var denominator = Vec.Cross(d1, d2);
        if (Math.Abs(denominator) > 1e-9 * length1 * length2)
        {
            var t = Vec.Cross(offset, d2) / denominator;
            var u = Vec.Cross(offset, d1) / denominator;
            var slack1 = tolerance / length1;
            var slack2 = tolerance / length2;
            if (t >= -slack1 && t <= 1 + slack1 && u >= -slack2 && u <= 1 + slack2)
            {
                firstSplits.Add(Math.Clamp(t, 0, 1));
                secondSplits.Add(Math.Clamp(u, 0, 1));
            }
            return;
        }

        // Parallel: only overlapping collinear walls share vertices.
        if (Math.Abs(Vec.Cross(offset, d1)) / length1 > tolerance)
        {
            return;
        }

        AddProjection(first.Start, d1, second.Start, firstSplits);
        AddProjection(first.Start, d1, second.End, firstSplits);
        AddProjection(second.Start, d2, first.Start, secondSplits);
        AddProjection(second.Start, d2, first.End, secondSplits);
    }

    private static void AddProjection(Vec origin, Vec direction, Vec point, List<double> splits)
    {
        var t = Vec.Dot(point - origin, direction) / Vec.Dot(direction, direction);
        if (t > 0 && t < 1)
        {
            splits.Add(t);
        }
    }

    private static List<ClosedGap> CloseGaps(
        PlanarGraph graph,
        double maxOpeningWidth,
        double maxDraftingGap
    )
    {
        var ends = Enumerable.Range(0, graph.Vertices.Count)
            .Where(index => graph.Degree(index) == 1)
            .ToList();
        var alignment = Math.Cos(OpeningAlignmentDegrees * Math.PI / 180);
        var candidates = new List<(double Distance, int A, int B, string Kind)>();
        for (var i = 0; i < ends.Count; i++)
        {
            for (var j = i + 1; j < ends.Count; j++)
            {
                var a = ends[i];
                var b = ends[j];
                var gap = graph.Vertices[b] - graph.Vertices[a];
                var distance = gap.Length;
                if (distance <= maxDraftingGap)
                {
                    candidates.Add((distance, a, b, "drafting_gap"));
                    continue;
                }
                if (distance > maxOpeningWidth)
                {
                    continue;
                }

                // Both wall ends must point at each other along the gap.
                var outA = graph.OutwardDirection(a);
                var outB = graph.OutwardDirection(b);
                var along = gap * (1 / distance);
                if (Vec.Dot(outA, along) >= alignment && Vec.Dot(outB, along * -1) >= alignment)
                {
                    candidates.Add((distance, a, b, "opening"));
                }
            }
        }

        var used = new HashSet<int>();
        var closed = new List<ClosedGap>();
        foreach (var candidate in candidates.OrderBy(item => item.Distance))
        {
            if (used.Contains(candidate.A) || used.Contains(candidate.B))
            {
                continue;
            }

            used.Add(candidate.A);
            used.Add(candidate.B);
            graph.AddEdge(candidate.A, candidate.B);
            closed.Add(new ClosedGap(
                candidate.Kind,
                graph.Vertices[candidate.A],
                graph.Vertices[candidate.B],
                candidate.Distance
            ));
        }

        return closed;
    }

    private static List<RoomFace> BuildRooms(
        PlanarGraph graph,
        List<List<int>> faces,
        double minArea,
        double minWidth
    )
    {
        var components = graph.Components();
        var bounded = new List<(List<Vec> Polygon, double Area, int Component)>();
        var outlines = new List<(List<Vec> Polygon, double Area, int Component)>();
        foreach (var face in faces)
        {
            var polygon = face.Select(index => graph.Vertices[index]).ToList();
            var area = SignedArea(polygon);
            var component = components[face[0]];
            if (area > 0)
            {
                bounded.Add((polygon, area, component));
            }
            else if (area < 0)
            {
                outlines.Add((polygon, -area, component));
            }
        }

        // The outline of a separate wall group inside a room (a column or a
        // shaft) is a hole in the smallest room that contains it.
        var holes = bounded.Select(_ => new List<List<Vec>>()).ToArray();
        foreach (var outline in outlines)
        {
            var owner = -1;
            for (var i = 0; i < bounded.Count; i++)
            {
                if (bounded[i].Component != outline.Component
                    && bounded[i].Area > outline.Area
                    && InPolygon(bounded[i].Polygon, outline.Polygon[0])
                    && (owner < 0 || bounded[i].Area < bounded[owner].Area))
                {
                    owner = i;
                }
            }
            if (owner >= 0)
            {
                holes[owner].Add(outline.Polygon);
            }
        }

        var rooms = new List<RoomFace>();
        for (var i = 0; i < bounded.Count; i++)
        {
            var (polygon, area, _) = bounded[i];
            var perimeter = Perimeter(polygon);
            var netArea = area - holes[i].Sum(hole => Math.Abs(SignedArea(hole)));
            // 2 * area / perimeter approximates the width of a thin strip, so
            // the space between the two lines of a wall is not a room, even
            // when that space is a ring around the room.
            var fullPerimeter = perimeter + holes[i].Sum(Perimeter);
            if (netArea < minArea || 2 * netArea / fullPerimeter < minWidth)
            {
                continue;
            }

            rooms.Add(new RoomFace(polygon, holes[i], area, netArea, perimeter, Centroid(polygon, area)));
        }

        return rooms.OrderByDescending(room => room.NetArea).ToList();
    }

    private static double Perimeter(IReadOnlyList<Vec> polygon)
    {
        var sum = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            sum += (polygon[(i + 1) % polygon.Count] - polygon[i]).Length;
        }

        return sum;
    }

    private static Vec Centroid(IReadOnlyList<Vec> polygon, double area)
    {
        double x = 0, y = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var cross = Vec.Cross(a, b);
            x += (a.X + b.X) * cross;
            y += (a.Y + b.Y) * cross;
        }

        return new Vec(x / (6 * area), y / (6 * area));
    }

    private sealed class PlanarGraph
    {
        private readonly double _tolerance;
        private readonly Dictionary<(long, long), List<int>> _grid = new();
        private readonly List<HashSet<int>> _neighbors = new();

        internal PlanarGraph(double tolerance)
        {
            _tolerance = tolerance;
        }

        internal List<Vec> Vertices { get; } = new();
        internal List<string> Sources { get; } = new();

        internal int Degree(int vertex) => _neighbors[vertex].Count;

        // Points closer than the tolerance become one vertex.
        internal int AddVertex(Vec point, string source)
        {
            var cellX = (long)Math.Floor(point.X / _tolerance);
            var cellY = (long)Math.Floor(point.Y / _tolerance);
            for (var dx = -1L; dx <= 1; dx++)
            {
                for (var dy = -1L; dy <= 1; dy++)
                {
                    if (!_grid.TryGetValue((cellX + dx, cellY + dy), out var cell))
                    {
                        continue;
                    }
                    foreach (var index in cell)
                    {
                        if ((Vertices[index] - point).Length <= _tolerance)
                        {
                            return index;
                        }
                    }
                }
            }

            Vertices.Add(point);
            Sources.Add(source);
            _neighbors.Add(new HashSet<int>());
            if (!_grid.TryGetValue((cellX, cellY), out var list))
            {
                list = new List<int>();
                _grid[(cellX, cellY)] = list;
            }
            list.Add(Vertices.Count - 1);
            return Vertices.Count - 1;
        }

        internal void AddEdge(int a, int b)
        {
            if (a == b)
            {
                return;
            }

            _neighbors[a].Add(b);
            _neighbors[b].Add(a);
        }

        // Unit vector from the only neighbour of a dangling end out past it.
        internal Vec OutwardDirection(int vertex)
        {
            var neighbor = _neighbors[vertex].First();
            var direction = Vertices[vertex] - Vertices[neighbor];
            return direction * (1 / direction.Length);
        }

        internal void PruneDanglingEdges()
        {
            var queue = new Queue<int>(
                Enumerable.Range(0, Vertices.Count).Where(index => _neighbors[index].Count == 1)
            );
            while (queue.Count > 0)
            {
                var vertex = queue.Dequeue();
                if (_neighbors[vertex].Count != 1)
                {
                    continue;
                }

                var neighbor = _neighbors[vertex].First();
                _neighbors[vertex].Clear();
                _neighbors[neighbor].Remove(vertex);
                if (_neighbors[neighbor].Count == 1)
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        internal int[] Components()
        {
            var component = Enumerable.Repeat(-1, Vertices.Count).ToArray();
            var next = 0;
            for (var start = 0; start < Vertices.Count; start++)
            {
                if (component[start] >= 0)
                {
                    continue;
                }

                var stack = new Stack<int>();
                stack.Push(start);
                component[start] = next;
                while (stack.Count > 0)
                {
                    foreach (var neighbor in _neighbors[stack.Pop()])
                    {
                        if (component[neighbor] < 0)
                        {
                            component[neighbor] = next;
                            stack.Push(neighbor);
                        }
                    }
                }
                next++;
            }

            return component;
        }

        // Walks every face of the planar graph. Bounded faces come out
        // counter-clockwise (positive area); the outline of each connected
        // wall group comes out clockwise (negative area).
        internal List<List<int>> TraceFaces()
        {
            var ordered = new List<int>[Vertices.Count];
            var position = new Dictionary<(int, int), int>();
            for (var vertex = 0; vertex < Vertices.Count; vertex++)
            {
                var origin = Vertices[vertex];
                ordered[vertex] = _neighbors[vertex]
                    .OrderBy(other => Math.Atan2(Vertices[other].Y - origin.Y, Vertices[other].X - origin.X))
                    .ToList();
                for (var i = 0; i < ordered[vertex].Count; i++)
                {
                    position[(vertex, ordered[vertex][i])] = i;
                }
            }

            var visited = new HashSet<(int, int)>();
            var faces = new List<List<int>>();
            foreach (var start in position.Keys)
            {
                if (visited.Contains(start))
                {
                    continue;
                }

                var face = new List<int>();
                var current = start;
                while (visited.Add(current))
                {
                    var (from, to) = current;
                    face.Add(from);
                    var around = ordered[to];
                    var next = around[(position[(to, from)] - 1 + around.Count) % around.Count];
                    current = (to, next);
                }
                faces.Add(face);
            }

            return faces;
        }
    }
}
