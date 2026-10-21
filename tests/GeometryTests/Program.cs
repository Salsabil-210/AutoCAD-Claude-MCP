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

// Bent bars --------------------------------------------------------------------

double Rad(double degrees) => degrees * Math.PI / 180;

// An L: 50 along +X, a 90 degree left turn at R10, 40 up. 2 thick.
var lBar = BentBarGeometry.Build(new Vec(0, 0), 0, new List<BarStep>
{
    new(BentBarGeometry.Line, 50, 0, 0),
    new(BentBarGeometry.Arc, 0, 10, Rad(90)),
    new(BentBarGeometry.Line, 40, 0, 0)
}, 2);
Check("bar: developed length = lines + arc", Near(lBar.Length, 90 + 10 * Math.PI / 2), lBar.Length.ToString("0.###"));
Check("bar: arc ends tangent, path ends at (60, 50)", Near(lBar.Pieces[2].End.X, 60) && Near(lBar.Pieces[2].End.Y, 50) && Near(lBar.Pieces[1].Center.X, 50) && Near(lBar.Pieces[1].Center.Y, 10),
    $"{lBar.Pieces[2].End} centre {lBar.Pieces[1].Center}");
Check("bar: bend starts at 50 along the developed length", Near(lBar.Pieces[1].StartDistance, 50) && Near(lBar.Pieces[2].StartDistance, 50 + 5 * Math.PI));
Check("bar: plan outline is 8 vertices with inner and outer bend arcs", lBar.Outline.Vertices.Count == 8
    && lBar.Outline.Bulges.Count(bulge => Near(bulge, Math.Tan(Rad(22.5)))) == 1 && lBar.Outline.Bulges.Count(bulge => Near(bulge, -Math.Tan(Rad(22.5)))) == 1,
    $"{lBar.Outline.Vertices.Count} vertices");
Check("bar: outline length = both sides + both caps", Near(lBar.Outline.Length, 2 * 90 + 9 * Math.PI / 2 + 11 * Math.PI / 2 + 2 * 2), lBar.Outline.Length.ToString("0.###"));
Check("bar: plan bounds include the outer bend", Near(lBar.Min.X, 0) && Near(lBar.Min.Y, -1) && Near(lBar.Max.X, 61) && Near(lBar.Max.Y, 50), $"{lBar.Min} {lBar.Max}");

var rightTurn = BentBarGeometry.Build(new Vec(0, 0), 0, new List<BarStep> { new(BentBarGeometry.Arc, 0, 10, Rad(-90)) }, 2);
Check("bar: right turn goes clockwise", Near(rightTurn.Pieces[0].End.X, 10) && Near(rightTurn.Pieces[0].End.Y, -10), rightTurn.Pieces[0].End.ToString());

Throws("bar: tight bend refused", () => BentBarGeometry.Build(new Vec(0, 0), 0, new List<BarStep> { new(BentBarGeometry.Arc, 0, 1, Rad(90)) }, 2), "half the thickness");
Throws("bar: zero sweep refused", () => BentBarGeometry.Build(new Vec(0, 0), 0, new List<BarStep> { new(BentBarGeometry.Arc, 0, 10, 0) }, 2), "between 0 and 360");

var plainElevation = BentBarGeometry.Elevation(100, 3, BentBarGeometry.Plain, BentBarGeometry.Plain, 0);
Check("bar elevation: plain ends give a rectangle", plainElevation.Outline.Vertices.Count == 4 && Near(plainElevation.Max.X, 100) && Near(plainElevation.Max.Y, 3) && Near(plainElevation.Min.Y, 0));

// Job 435, decorative arrow: 106.1 long in elevation A, 3 tall, arrowhead 30.9.
var arrow = BentBarGeometry.Elevation(106.1, 3, BentBarGeometry.Plain, BentBarGeometry.Arrow, 30.9);
var tip = arrow.Outline.Vertices.MaxBy(vertex => vertex.X);
Check("bar 435: arrow tip at the end, on the shaft centre", Near(tip.X, 106.1) && Near(tip.Y, 1.5), tip.ToString());
Check("bar 435: arrowhead spans 30.9", Near(arrow.Max.Y - arrow.Min.Y, 30.9) && Near(arrow.Outline.Vertices.Max(v => v.Y) - arrow.Outline.Vertices.Min(v => v.Y), 30.9));
Check("bar 435: arms are 3 wide at 45 degrees", arrow.Outline.Vertices.Count == 9
    && Near((arrow.Outline.Vertices[2] - arrow.Outline.Vertices[1]).Length, 3), (arrow.Outline.Vertices[2] - arrow.Outline.Vertices[1]).Length.ToString("0.###"));
var both = BentBarGeometry.Elevation(106.1, 3, BentBarGeometry.Arrow, BentBarGeometry.Arrow, 30.9);
Check("bar elevation: arrows at both ends mirror", both.Outline.Vertices.Count == 14 && Near(both.Outline.Vertices.Min(v => v.X), 0));
Throws("bar elevation: flat arrowhead refused", () => BentBarGeometry.Elevation(100, 3, BentBarGeometry.Plain, BentBarGeometry.Arrow, 6), "arrowHeight must be more than");
Throws("bar elevation: arrows longer than the bar refused", () => BentBarGeometry.Elevation(30, 3, BentBarGeometry.Arrow, BentBarGeometry.Arrow, 30), "too short");

// Face graphics -------------------------------------------------------------------

// Job 435, decorative chandelier face A: 130 long, 10 band, two chevrons
// 16 tall (3 proud top and bottom) starting 25 from the left end.
var row = FaceGraphicGeometry.Chevrons(130, 10, 25, 2, 5, 8, 5, 16, true);
Check("chevrons 435: row runs 25 to 43, 3 proud of the band", Near(row.Start, 25) && Near(row.End, 43) && Near(row.Bottom, -3) && Near(row.Top, 13),
    $"{row.Start}..{row.End}, {row.Bottom}..{row.Top}");
Check("chevrons: second one starts one pitch later, tip ahead", Near(row.Chevrons[1].Back, 33) && Near(row.Chevrons[1].Tip, 43) && row.Chevrons[0].Outline.Count == 6);
var leftRow = FaceGraphicGeometry.Chevrons(130, 10, 25, 2, 5, 8, 5, 16, false);
Check("chevrons: pointing left mirrors inside the same span", Near(leftRow.Chevrons.Min(chevron => chevron.Tip), 25) && Near(leftRow.Chevrons.Max(chevron => chevron.Back), 43)
    && leftRow.Chevrons.All(chevron => chevron.Tip < chevron.Back) && Near(leftRow.End, 43));
Throws("bar elevation: messages use a decimal point", () => BentBarGeometry.Elevation(100, 3, BentBarGeometry.Plain, BentBarGeometry.Arrow, 6), "7.243");
Throws("chevrons: past the face end refused", () => FaceGraphicGeometry.Chevrons(130, 10, 120, 2, 5, 8, 5, 16, true), "outside the face");
Throws("chevrons: overlapping refused", () => FaceGraphicGeometry.Chevrons(130, 10, 25, 2, 5, 3, 5, 16, true), "overlap");

// Spec check ----------------------------------------------------------------------

var repository = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
while (repository is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(repository.FullName, "skills")))
{
    repository = repository.Parent;
}
var examplePath = System.IO.Path.Combine(repository!.FullName, "skills", "ceiling-feature-production", "examples", "435-chandelier.json");
using var example = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(examplePath));
var report = SpecCheck.Check(example.RootElement);
string Findings(SpecReport checkedSpec) => string.Join(" | ", checkedSpec.Findings.Select(finding => $"{finding.Severity} {finding.Part}: {finding.Message}"));
Check("spec 435: drawable, no errors", report.Drawable && report.Findings.All(finding => finding.Severity != SpecCheck.Error), Findings(report));
Check("spec 435: not issuable (TBD colours, open questions, unconfirmed)", !report.Issuable
    && report.Findings.Count(finding => finding.Part == "materials" && finding.Severity == SpecCheck.Warning) == 2
    && report.Findings.Any(finding => finding.Part == "openQuestions")
    && report.Findings.Count(finding => finding.Message.Contains("not confirmed")) == 3);
Check("spec 435: arrow developed length 161.9", report.Parts.Single(part => (string?)part["id"] == "P3")["developedLength"] is double arrowLength && Near(arrowLength, 161.9),
    report.Parts.Single(part => (string?)part["id"] == "P3")["developedLength"]?.ToString() ?? "none");
Check("spec 435: chevrons stay on the flat part of each face", !report.Findings.Any(finding => finding.Message.Contains("fillet")), Findings(report));

SpecReport CheckJson(string json)
{
    using var document = System.Text.Json.JsonDocument.Parse(json);
    return SpecCheck.Check(document.RootElement);
}
const string BrokenSpec = """
{
  "job": { "number": "1", "site": "s", "title": "t", "date": "d", "units": "cm" },
  "materials": [{ "id": "M1", "ral": "5015", "aci": 5 }],
  "parts": [
    { "id": "P1", "type": "band", "name": "Ring", "quantity": 1, "material": "M1",
      "points": [{ "x": 0, "y": 0 }, { "x": 100, "y": 0 }, { "x": 100, "y": 100 }, { "x": 0, "y": 100 }],
      "cornerRadius": 15, "thickness": 5, "height": 10,
      "faceGraphics": [
        { "face": "A", "kind": "chevrons", "at": 2, "count": 2, "stroke": 5, "pitch": 13, "depth": 5, "chevronHeight": 16 },
        { "face": "E", "kind": "letters", "text": "X", "letterHeight": 5 },
        { "face": "B", "kind": "letters", "text": "COFFEE", "letterHeight": 20 }
      ] },
    { "id": "P2", "type": "bent_bar", "name": "Arrow", "quantity": 0, "material": "M9",
      "start": { "x": 0, "y": 0 }, "segments": [{ "kind": "arc", "radius": 1, "angle": 90 }], "thickness": 3, "height": 3 }
  ],
  "assembly": [{ "part": "P7" }],
  "openQuestions": []
}
""";
var broken = CheckJson(BrokenSpec);
bool Has(SpecReport checkedSpec, string severity, string part, string text) =>
    checkedSpec.Findings.Any(finding => finding.Severity == severity && finding.Part == part && finding.Message.Contains(text));
Check("spec: broken spec is not drawable", !broken.Drawable && !broken.Issuable, Findings(broken));
Check("spec: chevrons on the corner fillet warned", Has(broken, SpecCheck.Warning, "P1", "corner fillet"), Findings(broken));
Check("spec: unknown face refused", Has(broken, SpecCheck.Error, "P1", "face E"));
Check("spec: letters taller than the band refused", Has(broken, SpecCheck.Error, "P1", "taller than"));
Check("spec: equal faces with different graphics need groupFaces false", Has(broken, SpecCheck.Error, "P1", "groupFaces: false"), Findings(broken));
Check("spec: bad quantity and material refused", Has(broken, SpecCheck.Error, "P2", "quantity") && Has(broken, SpecCheck.Error, "P2", "M9"));
Check("spec: geometry errors come from the drawing geometry", Has(broken, SpecCheck.Error, "P2", "half the thickness"));
Check("spec: assembly must name real parts", Has(broken, SpecCheck.Error, "assembly", "P7"));
Check("spec: assembly without hangers warned", Has(broken, SpecCheck.Warning, "hangers", "No hanger points"));
var hung = CheckJson(BrokenSpec.Replace("\"openQuestions\": []", "\"hangers\": [{ \"x\": 1, \"y\": 2, \"drop\": 120 }, { \"x\": 3, \"y\": 4 }], \"openQuestions\": []"));
Check("spec: hanger without a drop length warned", Has(hung, SpecCheck.Warning, "hangers", "1 hanger point(s) have no drop") && !Has(hung, SpecCheck.Warning, "hangers", "No hanger points"), Findings(hung));
Check("spec: not an object", !CheckJson("[]").Drawable);
var estimatedSpec = CheckJson(BrokenSpec.Replace("\"cornerRadius\": 15, \"thickness\": 5, \"height\": 10,",
    "\"cornerRadius\": 15, \"thickness\": 5, \"height\": 10, \"source\": { \"render\": \"1\", \"confirmed\": true, \"estimated\": [\"height\", \"cornerRadius\"] },"));
Check("spec: estimated values block issuing", Has(estimatedSpec, SpecCheck.Warning, "P1", "Estimated, not given by the designer: height, cornerRadius"), Findings(estimatedSpec));

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures;
