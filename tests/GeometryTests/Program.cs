using System;
using System.Collections.Generic;
using System.Linq;
using AutoCADClaudeBridge;

// Regression tests for FloorPlanGeometry (room detection), WallGeometry
// (wall drawing) and BandGeometry (ceiling band parts). Run from this folder
// with: dotnet run -c Release
var failures = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : "")}");
    if (!ok)
    {
        failures++;
    }
}

bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-6;

WallSegment W(double x1, double y1, double x2, double y2) => new(new Vec(x1, y1), new Vec(x2, y2), "W");

RoomDetection Rooms(List<WallSegment> walls) => FloorPlanGeometry.DetectRooms(walls, 1e-6, 1.2, 0.02, 0.5, 0.5);

string Areas(RoomDetection result) => string.Join(", ", result.Rooms.Select(room => room.NetArea.ToString("0.###")));

void Throws(string name, Action action, string expected)
{
    try
    {
        action();
        Check(name, false, "no error");
    }
    catch (ArgumentException exception)
    {
        Check(name, exception.Message.Contains(expected), exception.Message);
    }
}

// Room detection -------------------------------------------------------------

var result = Rooms(new() { W(0, 0, 10, 0), W(10, 0, 10, 6), W(10, 6, 0, 6), W(0, 6, 0, 0), W(4, 0, 4, 6) });
Check("rooms: partition", result.Rooms.Count == 2 && Near(result.Rooms[0].NetArea, 36) && Near(result.Rooms[1].NetArea, 24) && result.Gaps.Count == 0, Areas(result));

result = Rooms(new() { W(0, 0, 10, 0), W(10, 0, 10, 6), W(10, 6, 0, 6), W(0, 6, 0, 0), W(4, 0, 4, 2), W(4, 2.9, 4, 6) });
Check("rooms: door opening closed", result.Rooms.Count == 2 && result.Closed.Count == 1 && result.Closed[0].Kind == "opening" && Near(result.Closed[0].Width, 0.9), Areas(result));

result = Rooms(new() { W(0, 0, 10, 0), W(10, 0, 10, 6), W(10, 6, 0, 6), W(0, 6, 0, 0), W(4, 0, 4, 2), W(4, 4, 4, 6) });
Check("rooms: wide gap flagged", result.Rooms.Count == 1 && Near(result.Rooms[0].NetArea, 60) && result.Gaps.Count == 2, Areas(result));

result = Rooms(new() { W(0, 0, 5, 0), W(5, 0.01, 5, 4), W(5, 4, 0, 4), W(0, 4, 0, 0) });
Check("rooms: drafting gap closed", result.Rooms.Count == 1 && result.Closed.Count == 1 && result.Closed[0].Kind == "drafting_gap", Areas(result));

result = Rooms(new() {
    W(-0.2, -0.2, 5.2, -0.2), W(5.2, -0.2, 5.2, 4.2), W(5.2, 4.2, -0.2, 4.2), W(-0.2, 4.2, -0.2, -0.2),
    W(0, 0, 5, 0), W(5, 0, 5, 4), W(5, 4, 0, 4), W(0, 4, 0, 0) });
Check("rooms: double walls", result.Rooms.Count == 1 && Near(result.Rooms[0].NetArea, 20), Areas(result));

result = Rooms(new() {
    W(0, 0, 10, 0), W(10, 0, 10, 6), W(10, 6, 0, 6), W(0, 6, 0, 0),
    W(4, 0, 4, 2), W(4, 2.9, 4, 6), W(4.2, 0, 4.2, 2), W(4.2, 2.9, 4.2, 6) });
Check("rooms: double-line door", result.Rooms.Count == 2 && result.Closed.Count(gap => gap.Kind == "opening") == 2 && result.Gaps.Count == 0, Areas(result));

result = Rooms(new() { W(0, 0, 6, 0), W(6, 0, 6, 6), W(6, 6, 0, 6), W(0, 6, 0, 0), W(2, 2, 3, 2), W(3, 2, 3, 3), W(3, 3, 2, 3), W(2, 3, 2, 2) });
Check("rooms: column hole", Near(result.Rooms[0].NetArea, 35) && result.Rooms[0].Holes.Count == 1, Areas(result));
Check("rooms: contains respects holes", FloorPlanGeometry.Contains(result.Rooms[0], new Vec(1, 1)) && !FloorPlanGeometry.Contains(result.Rooms[0], new Vec(2.5, 2.5)));

result = Rooms(new() { W(0, 0, 6, 0), W(6, 0, 6, 6), W(6, 6, 0, 6), W(0, 6, 0, 0), W(3, -0.5, 3, 6.5), W(-0.5, 3, 6.5, 3) });
Check("rooms: crossing walls", result.Rooms.Count == 4 && result.Rooms.All(room => Near(room.NetArea, 9)), Areas(result));

result = Rooms(new() { W(0, 0, 5, 0), W(1, 0, 4, 0), W(5, 0, 5, 4), W(5, 4, 0, 4), W(0, 4, 0, 0) });
Check("rooms: overlapping duplicate", result.Rooms.Count == 1 && Near(result.Rooms[0].NetArea, 20), Areas(result));

var grid = new List<WallSegment>();
for (var i = 0; i <= 60; i++)
{
    grid.Add(W(i * 3, 0, i * 3, 180));
    grid.Add(W(0, i * 3, 180, i * 3));
}
var watch = System.Diagnostics.Stopwatch.StartNew();
result = Rooms(grid);
Check("rooms: 3600-room grid", result.Rooms.Count == 3600 && watch.ElapsedMilliseconds < 5000, $"{watch.ElapsedMilliseconds} ms");

// Wall drawing -----------------------------------------------------------------

var none = new List<WallOpening>();
var box = new List<Vec> { new(0, 0), new(4, 0), new(4, 3), new(0, 3) };

List<WallSegment> WallLines(WallDrawing drawing, bool withSeparation) => drawing.Lines
    .Where(line => line.Role == PlanRole.Wall || (withSeparation && line.Role == PlanRole.Separation))
    .Select(line => new WallSegment(line.Start, line.End, "W"))
    .ToList();

var plain = WallGeometry.Build(box, true, 0.2, none, true);
result = Rooms(WallLines(plain, false));
Check("walls: closed box has 8 mitred lines", plain.Lines.Count == 8 && plain.Arcs.Count == 0, plain.Lines.Count.ToString());
Check("walls: closed box room = 3.8 x 2.8", result.Rooms.Count == 1 && Near(result.Rooms[0].NetArea, 10.64) && result.Gaps.Count == 0, Areas(result));

var door = WallGeometry.Build(box, true, 0.2, new List<WallOpening> { new(0, 1, 0.9, WallGeometry.Door, "start", "left") }, true);
var leaf = door.Lines.Single(line => line.Role == PlanRole.Door);
var swing = door.Arcs.Single();
Check("walls: door leaf from hinge on inner face", Near(leaf.Start.X, 1) && Near(leaf.Start.Y, 0.1) && Near(leaf.End.X, 1) && Near(leaf.End.Y, 1.0),
    $"{leaf.Start} -> {leaf.End}");
Check("walls: door swing 0..90 deg about hinge", Near(swing.Center.X, 1) && Near(swing.Center.Y, 0.1) && Near(swing.Radius, 0.9) && Near(swing.StartAngle, 0) && Near(swing.EndAngle, Math.PI / 2),
    $"{swing.StartAngle} {swing.EndAngle}");
Check("walls: door has caps and one separation line", door.Lines.Count(line => line.Role == PlanRole.Separation) == 1 && door.Lines.Count(line => line.Role == PlanRole.Wall) == 12,
    door.Lines.Count(line => line.Role == PlanRole.Wall).ToString());
result = Rooms(WallLines(door, true));
Check("walls: room through door is closed by separation line", result.Rooms.Count == 1 && Near(result.Rooms[0].NetArea, 10.64 + 0.9 * 0.1) && result.Gaps.Count == 0, Areas(result));
result = Rooms(WallLines(door, false));
Check("walls: without separation the capped door leaks", result.Rooms.Count == 0, Areas(result));

var hingeEnd = WallGeometry.Build(box, true, 0.2, new List<WallOpening> { new(0, 1, 0.9, WallGeometry.Door, "end", "right") }, true);
var leafEnd = hingeEnd.Lines.Single(line => line.Role == PlanRole.Door);
Check("walls: hinge end, swing right", Near(leafEnd.Start.X, 1.9) && Near(leafEnd.Start.Y, -0.1) && Near(leafEnd.End.Y, -1.0), $"{leafEnd.Start} -> {leafEnd.End}");

var window = WallGeometry.Build(box, true, 0.2, new List<WallOpening> { new(1, 1, 1.2, WallGeometry.Window, "start", "left") }, true);
Check("walls: window keeps wall lines, adds jambs and glass", window.Lines.Count(line => line.Role == PlanRole.Window) == 3
    && window.Lines.Count(line => line.Role == PlanRole.Wall) == 8 && !window.Lines.Any(line => line.Role == PlanRole.Separation) && window.Arcs.Count == 0);
result = Rooms(WallLines(window, true));
Check("walls: window does not change the room area", result.Rooms.Count == 1 && Near(result.Rooms[0].NetArea, 10.64), Areas(result));

Throws("walls: opening into corner refused",
    () => WallGeometry.Build(box, true, 0.2, new List<WallOpening> { new(0, 0, 0.5, WallGeometry.Opening, "start", "left") }, true), "corner");
Throws("walls: overlapping openings refused",
    () => WallGeometry.Build(box, true, 0.2, new List<WallOpening> { new(0, 1, 1, WallGeometry.Opening, "start", "left"), new(0, 1.5, 1, WallGeometry.Opening, "start", "left") }, true), "overlap");
Throws("walls: missing segment refused",
    () => WallGeometry.Build(box, true, 0.2, new List<WallOpening> { new(7, 1, 1, WallGeometry.Opening, "start", "left") }, true), "does not exist");

var exterior = WallGeometry.Build(new List<Vec> { new(0, 0), new(6, 0), new(6, 4), new(0, 4) }, true, 0.2, none, true);
var partition = WallGeometry.Build(new List<Vec> { new(3, 0.1), new(3, 3.9) }, false, 0.1, none, true);
result = Rooms(WallLines(exterior, false).Concat(WallLines(partition, false)).ToList());
Check("walls: partition between exterior faces makes two rooms", result.Rooms.Count == 2 && result.Rooms.All(room => Near(room.NetArea, 2.85 * 3.8)) && result.Gaps.Count == 0, Areas(result));

var corner = WallGeometry.Build(new List<Vec> { new(0, 0), new(4, 0), new(4, 3) }, false, 0.2, none, false);
Check("walls: open L path mitres its corner", corner.Lines.Any(line => Near(line.End.X, 4.1) && Near(line.End.Y, -0.1)) && corner.Lines.Count == 4, corner.Lines.Count.ToString());

// Band parts -------------------------------------------------------------------

List<double> Radii(int count, double radius) => Enumerable.Repeat(radius, count).ToList();

// A 100 x 60 rectangle band, R15 outside, 5 thick: inner R10.
var square = new List<Vec> { new(0, 0), new(100, 0), new(100, 60), new(0, 60) };
var band = BandGeometry.Build(square, Radii(4, 15), 5);
Check("band: faces labelled A..D from the first corner", string.Join("", band.Faces.Select(face => face.Label)) == "ABCD"
    && Near(band.Faces[0].EdgeLength, 100) && Near(band.Faces[1].EdgeLength, 60), string.Join(", ", band.Faces.Select(face => face.EdgeLength)));
Check("band: straight part excludes both fillets", Near(band.Faces[0].StraightLength, 70) && Near(band.Faces[1].StraightLength, 30));
Check("band: outer perimeter = straights + full circle", Near(band.Outer.Length, 2 * 70 + 2 * 30 + 2 * Math.PI * 15), band.Outer.Length.ToString("0.###"));
Check("band: inner perimeter with R10", Near(band.Inner.Length, 2 * 70 + 2 * 30 + 2 * Math.PI * 10), band.Inner.Length.ToString("0.###"));
Check("band: centreline perimeter with R12.5", Near(band.Centre.Length, 2 * 70 + 2 * 30 + 2 * Math.PI * 12.5), band.Centre.Length.ToString("0.###"));
Check("band: inner radius = outer - thickness", band.Corners.All(corner => Near(corner.InnerRadius, 10)));
Check("band: 8 outer vertices with 4 quarter-arc bulges", band.Outer.Vertices.Count == 8
    && band.Outer.Bulges.Count(bulge => Near(bulge, Math.Tan(Math.PI / 8))) == 4);
Check("band: first outer vertex is the tangent point on face D", Near(band.Outer.Vertices[0].X, 0) && Near(band.Outer.Vertices[0].Y, 15), band.Outer.Vertices[0].ToString());
Check("band: inner loop is inset by the thickness", Near(band.Inner.Vertices.Min(vertex => vertex.X), 5) && Near(band.Inner.Vertices.Max(vertex => vertex.Y), 55));
Check("band: face A outward is -Y", Near(band.Faces[0].Outward.X, 0) && Near(band.Faces[0].Outward.Y, -1));
var uneven = BandGeometry.Build(square, new List<double> { 0, 20, 0, 0 }, 5);
Check("band: flat midpoint sits in the middle of the flat part", Near(uneven.Faces[0].FlatMidpoint.X, 40) && Near(uneven.Faces[0].FlatMidpoint.Y, 0), uneven.Faces[0].FlatMidpoint.ToString());
Check("band: corner 0 outward points down-left", Near(band.Corners[0].Outward.X, -Math.Sqrt(0.5)) && Near(band.Corners[0].Outward.Y, -Math.Sqrt(0.5)));

var clockwise = BandGeometry.Build(square.AsEnumerable().Reverse().ToList(), Radii(4, 15), 5);
Check("band: clockwise corners give the same ring", Near(clockwise.Outer.Length, band.Outer.Length) && Near(clockwise.Inner.Length, band.Inner.Length)
    && clockwise.Outer.Bulges.Where(bulge => bulge != 0).All(bulge => bulge < 0) && Near(clockwise.Faces[0].Outward.Y, 1));

var sharpInside = BandGeometry.Build(square, Radii(4, 3), 5);
Check("band: radius under thickness leaves a sharp inner corner", sharpInside.Inner.Vertices.Count == 4 && sharpInside.Corners.All(corner => corner.InnerRadius == 0));

Throws("band: concave outline refused",
    () => BandGeometry.Build(new List<Vec> { new(0, 0), new(10, 0), new(5, 2), new(10, 10), new(0, 10) }, Radii(5, 0), 1), "convex");
Throws("band: self-crossing star refused",
    () => BandGeometry.Build(Enumerable.Range(0, 5).Select(i => new Vec(Math.Cos(i * 4 * Math.PI / 5), Math.Sin(i * 4 * Math.PI / 5))).ToList(), Radii(5, 0), 0.01), "crosses itself");
var touching = BandGeometry.Build(new List<Vec> { new(0, 0), new(30, 0), new(30, 60), new(0, 60) }, Radii(4, 15), 5);
Check("band: fillets that meet share one vertex", touching.Outer.Vertices.Count == 6 && touching.Faces[0].StraightLength < 1e-9
    && touching.Outer.Vertices.Zip(touching.Outer.Vertices.Skip(1).Append(touching.Outer.Vertices[0])).All(pair => (pair.First - pair.Second).Length > 1e-6)
    && Near(touching.Outer.Length, 2 * 30 + 2 * Math.PI * 15), $"{touching.Outer.Vertices.Count} vertices, {touching.Outer.Length:0.###}");
Throws("band: radii that do not fit refused", () => BandGeometry.Build(square, Radii(4, 40), 5), "do not fit");
Throws("band: too thick refused", () => BandGeometry.Build(square, Radii(4, 0), 31), "too thick");
Throws("band: wrong radius count refused", () => BandGeometry.Build(square, Radii(3, 15), 5), "one per corner");

// Job 435, decorative chandelier: a parallelogram, faces A and C 130 long,
// B and D 143.4, R15 outside, 5 thick.
var run = Math.Sqrt(130 * 130 - 27.7 * 27.7);
var decorative = BandGeometry.Build(new List<Vec> { new(0, 0), new(run, 27.7), new(run, 171.1), new(0, 143.4) }, Radii(4, 15), 5);
var groups = BandGeometry.GroupFaces(decorative.Faces, true, 0.05);
Check("band 435: decorative chandelier faces 130 / 143.4", Near(decorative.Faces[0].EdgeLength, 130) && Near(decorative.Faces[1].EdgeLength, 143.4)
    && Near(decorative.Faces[2].EdgeLength, 130) && Near(decorative.Faces[3].EdgeLength, 143.4), string.Join(", ", decorative.Faces.Select(face => face.EdgeLength.ToString("0.##"))));
Check("band 435: two elevations, A,C and B,D", groups.Count == 2 && string.Join(",", groups[0].Labels) == "A,C" && string.Join(",", groups[1].Labels) == "B,D",
    string.Join(" | ", groups.Select(group => string.Join(",", group.Labels))));
Check("band 435: fillets on a skewed outline still sum to one turn",
    Near(decorative.Outer.Length - decorative.Faces.Sum(face => face.StraightLength), 2 * Math.PI * 15), decorative.Outer.Length.ToString("0.###"));
Check("band: ungrouped gives one elevation per face", BandGeometry.GroupFaces(decorative.Faces, false, 0.05).Count == 4);

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures;
