using System;
using System.Collections.Generic;
using System.Linq;
using AutoCADClaudeBridge;

// Regression tests for FloorPlanGeometry (room detection) and WallGeometry
// (wall drawing). Run from this folder with: dotnet run -c Release
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

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures;
