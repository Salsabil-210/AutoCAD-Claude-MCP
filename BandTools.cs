using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADClaudeBridge;

// Ceiling feature production drawings: a band part (a closed ring of sheet
// material standing on edge) drawn as a production sheet section: plan with
// face letters and dimensions, plus one elevation per face or per group of
// identical faces, with an optional LED light strip.
internal static partial class BridgeHost
{
    private const string BandLayerDefault = "SIGN-BAND";
    private const string LightLayerDefault = "SIGN-LIGHT";
    private const string SignDimensionLayerDefault = "SIGN-DIM";
    private const string SignAnnotationLayerDefault = "SIGN-ANNO";
    private const short DefaultLightColor = 2;
    // Faces whose lengths agree within this many user units share an elevation.
    private const double FaceGroupTolerance = 0.05;

    private sealed record LightStrip(double Offset, double Height);

    private sealed class BandHandles
    {
        public List<string> Outline { get; } = new();
        public List<string> Fill { get; } = new();
        public List<string> Light { get; } = new();
        public List<string> Dimensions { get; } = new();
        public List<string> Annotations { get; } = new();
    }

    private static string DrawBandPart(JsonElement arguments)
    {
        var document = RequireDocument();
        var database = document.Database;
        var units = Units(arguments, database);
        var name = RequiredText(arguments, "name").Trim();
        var quantity = OptionalInteger(arguments, "quantity", 1, 1, 10_000);
        var corners = ReadPolygon(arguments, "points", units.Scale)
            .Select(point => new Vec(point.X, point.Y))
            .ToList();
        var thickness = PositiveSize(arguments, "thickness", units.Scale);
        var height = PositiveSize(arguments, "height", units.Scale);
        var radii = ReadCornerRadii(arguments, corners.Count, units.Scale);
        var light = ReadLightStrip(arguments, height, units.Scale);
        var groupFaces = OptionalBoolean(arguments, "groupFaces", true);
        var fillColor = OptionalColorIndex(arguments, "fillColor");
        var lightColor = OptionalColorIndex(arguments, "lightColor") ?? Color.FromColorIndex(ColorMethod.ByAci, DefaultLightColor);
        var bandLayer = OptionalName(arguments, "bandLayer", "bandLayer") ?? BandLayerDefault;
        var lightLayer = OptionalName(arguments, "lightLayer", "lightLayer") ?? LightLayerDefault;
        var dimensionLayer = OptionalName(arguments, "dimensionLayer", "dimensionLayer") ?? SignDimensionLayerDefault;
        var annotationLayer = OptionalName(arguments, "annotationLayer", "annotationLayer") ?? SignAnnotationLayerDefault;

        var part = BandGeometry.Build(corners, radii, thickness);
        var elevations = BandGeometry.GroupFaces(part.Faces, groupFaces, FaceGroupTolerance * units.Scale);

        var planMin = new Vec(corners.Min(point => point.X), corners.Min(point => point.Y));
        var planMax = new Vec(corners.Max(point => point.X), corners.Max(point => point.Y));
        var extent = Math.Max(Math.Max(planMax.X - planMin.X, planMax.Y - planMin.Y), elevations.Max(elevation => elevation.Length));
        var s = OptionalPositive(arguments, "textHeight", units.Scale) ?? extent / 40;
        string Format(double drawingValue) => (drawingValue / units.Scale).ToString("0.0", CultureInfo.InvariantCulture);

        using var documentLock = document.LockDocument();
        using var transaction = database.TransactionManager.StartTransaction();
        foreach (var layerName in new[] { bandLayer, lightLayer, dimensionLayer, annotationLayer }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            EnsureLayer(database, transaction, layerName);
        }
        var modelSpace = OpenModelSpace(database, transaction);
        var handles = new BandHandles();
        string Add(Entity entity, string layerName, Color? color = null)
        {
            entity.Layer = layerName;
            if (color is not null)
            {
                entity.Color = color;
            }
            modelSpace.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            return entity.Handle.ToString();
        }
        string Note(Vec location, double textHeight, string contents, AttachmentPoint attachment)
        {
            using var mtext = new MText();
            mtext.SetDatabaseDefaults();
            mtext.Location = ToPoint(location);
            mtext.TextHeight = textHeight;
            mtext.Attachment = attachment;
            mtext.Contents = MTextContents(contents);
            return Add(mtext, annotationLayer);
        }
        string Measure(Dimension dimension)
        {
            dimension.SetDatabaseDefaults();
            var handle = Add(dimension, dimensionLayer);
            StyleBandDimension(dimension, s, units.Scale);
            return handle;
        }
        string Marker(Vec tip, Vec pointing)
        {
            // A filled triangle whose tip touches `tip`, pointing along `pointing`.
            var side = new Vec(-pointing.Y, pointing.X) * (s * 0.6);
            var baseCentre = tip - pointing * (s * 1.2);
            using var solid = new Solid(ToPoint(tip), ToPoint(baseCentre + side), ToPoint(baseCentre - side), ToPoint(baseCentre - side));
            return Add(solid, annotationLayer);
        }

        // Plan ---------------------------------------------------------------
        using (var outer = LoopPolyline(part.Outer))
        {
            handles.Outline.Add(Add(outer, bandLayer));
        }
        using (var inner = LoopPolyline(part.Inner))
        {
            handles.Outline.Add(Add(inner, bandLayer));
        }
        if (fillColor is not null)
        {
            handles.Fill.Add(AddSolidHatch(modelSpace, transaction, bandLayer, fillColor, part.Outer, part.Inner));
        }

        foreach (var face in part.Faces)
        {
            var dimensionLine = face.Midpoint + face.Outward * (3 * s);
            using var dimension = new AlignedDimension(ToPoint(face.Start), ToPoint(face.End), ToPoint(dimensionLine), string.Empty, database.Dimstyle);
            handles.Dimensions.Add(Measure(dimension));

            var markerTip = face.Midpoint + face.Outward * (5.5 * s);
            handles.Annotations.Add(Marker(markerTip, face.Outward * -1));
            handles.Annotations.Add(Note(markerTip + face.Outward * (2.2 * s), s, face.Label, AttachmentPoint.MiddleCenter));
        }

        // Band thickness, measured on the flat part of face A.
        var firstFace = part.Faces[0];
        var faceDirection = (firstFace.End - firstFace.Start) * (1 / firstFace.EdgeLength);
        using (var thicknessDimension = new AlignedDimension(
            ToPoint(firstFace.FlatMidpoint),
            ToPoint(firstFace.FlatMidpoint - firstFace.Outward * thickness),
            ToPoint(firstFace.FlatMidpoint + faceDirection * (2 * s)),
            string.Empty,
            database.Dimstyle))
        {
            handles.Dimensions.Add(Measure(thicknessDimension));
        }

        // One radius note per distinct pair of outer and inner radii.
        var labelled = new HashSet<string>();
        foreach (var corner in part.Corners.Where(corner => corner.OuterRadius > 0))
        {
            var note = corner.InnerRadius > 0
                ? $"R{Format(corner.OuterRadius)} OUT / R{Format(corner.InnerRadius)} IN"
                : $"R{Format(corner.OuterRadius)} OUT / SHARP IN";
            if (labelled.Add(note))
            {
                var attachment = corner.Outward.X >= 0 ? AttachmentPoint.MiddleLeft : AttachmentPoint.MiddleRight;
                handles.Annotations.Add(Note(corner.Point + corner.Outward * (1.5 * s), s * 0.8, note, attachment));
            }
        }

        var title = $"{name.ToUpperInvariant()}    x{quantity}";
        var titleTop = planMax.Y + 10 * s;
        handles.Annotations.Add(Note(new Vec(planMin.X - 8 * s, titleTop), s * 1.5, title, AttachmentPoint.BottomLeft));

        var summary = new List<string>
        {
            $"BAND {Format(thickness)} THK x {Format(height)} H",
            $"DEVELOPED LENGTH (CENTRELINE) {Format(part.Centre.Length)}",
            $"OUTER {Format(part.Outer.Length)} / INNER {Format(part.Inner.Length)}"
        };
        if (light is not null)
        {
            summary.Add($"LED STRIP {Format(light.Height)} AT {Format(light.Offset)} FROM BOTTOM");
        }
        handles.Annotations.Add(Note(new Vec(planMin.X - 8 * s, planMin.Y - 10 * s), s * 0.8, string.Join("\n", summary), AttachmentPoint.TopLeft));

        // Elevations ---------------------------------------------------------
        var left = planMax.X + 16 * s;
        var cursor = titleTop;
        var elevationRight = left;
        foreach (var elevation in elevations)
        {
            var labels = string.Join(",", elevation.Labels);
            handles.Annotations.Add(Note(new Vec(left, cursor), s * 1.2, $"ELEVATION \"{labels}\"", AttachmentPoint.BottomLeft));
            var top = cursor - 3 * s;
            var bottom = top - height;
            var right = left + elevation.Length;
            elevationRight = Math.Max(elevationRight, right + 10 * s);

            var rectangle = new List<Vec> { new(left, bottom), new(right, bottom), new(right, top), new(left, top) };
            var rectangleLoop = new BandLoop(rectangle, new List<double> { 0, 0, 0, 0 }, 0);
            using (var outline = LoopPolyline(rectangleLoop))
            {
                handles.Outline.Add(Add(outline, bandLayer));
            }
            if (fillColor is not null)
            {
                handles.Fill.Add(AddSolidHatch(modelSpace, transaction, bandLayer, fillColor, rectangleLoop, null));
            }

            using (var lengthDimension = new RotatedDimension(0, new Point3d(left, bottom, 0), new Point3d(right, bottom, 0), new Point3d(left, bottom - 3 * s, 0), string.Empty, database.Dimstyle))
            {
                handles.Dimensions.Add(Measure(lengthDimension));
            }
            using (var heightDimension = new RotatedDimension(Math.PI / 2, new Point3d(right, bottom, 0), new Point3d(right, top, 0), new Point3d(right + 6 * s, bottom, 0), string.Empty, database.Dimstyle))
            {
                handles.Dimensions.Add(Measure(heightDimension));
            }

            if (light is not null)
            {
                var stripBottom = bottom + light.Offset;
                var stripTop = stripBottom + light.Height;
                var strip = new BandLoop(
                    new List<Vec> { new(left, stripBottom), new(right, stripBottom), new(right, stripTop), new(left, stripTop) },
                    new List<double> { 0, 0, 0, 0 },
                    0
                );
                handles.Light.Add(AddSolidHatch(modelSpace, transaction, lightLayer, lightColor, strip, null));
                foreach (var y in new[] { stripBottom, stripTop })
                {
                    using var line = new Line(new Point3d(left, y, 0), new Point3d(right, y, 0));
                    handles.Light.Add(Add(line, lightLayer));
                }
                foreach (var (from, to) in new[] { (bottom, stripBottom), (stripBottom, stripTop), (stripTop, top) })
                {
                    if (to - from <= Tolerance)
                    {
                        continue;
                    }
                    using var chain = new RotatedDimension(Math.PI / 2, new Point3d(right, from, 0), new Point3d(right, to, 0), new Point3d(right + 3 * s, from, 0), string.Empty, database.Dimstyle);
                    handles.Dimensions.Add(Measure(chain));
                }
            }

            var below = bottom - 6 * s;
            if (Math.Abs(elevation.StraightLength - elevation.Length) > FaceGroupTolerance * units.Scale)
            {
                handles.Annotations.Add(Note(new Vec(right, below), s * 0.8, $"FLAT {Format(elevation.StraightLength)} + CORNER RADII", AttachmentPoint.TopRight));
            }
            var markerTip = new Vec((left + right) / 2, below + s * 0.5);
            handles.Annotations.Add(Marker(markerTip, new Vec(0, 1)));
            handles.Annotations.Add(Note(markerTip - new Vec(0, 1.6 * s), s, labels, AttachmentPoint.TopCenter));

            cursor = below - 6 * s;
        }

        transaction.Commit();

        // Radius notes on left-hand corners run up to about 12 text heights left of the plan.
        var minimum = new Vec(planMin.X - 14 * s, Math.Min(planMin.Y - 16 * s, cursor));
        var maximum = new Vec(Math.Max(planMax.X + 8 * s, elevationRight), titleTop + 2 * s);
        var factor = 1 / units.Scale;
        document.Editor.WriteMessage($"\nClaude drew band part \"{name}\" with {elevations.Count} elevations. Use UNDO to remove it.");
        return Success(
            $"Band part \"{name}\" drawn: plan and {elevations.Count} elevation(s).",
            new
            {
                name,
                quantity,
                units = units.Label,
                thickness = thickness * factor,
                height = height * factor,
                lightStrip = light is null ? null : new { offset = light.Offset * factor, height = light.Height * factor },
                faces = part.Faces.Select(face => new
                {
                    label = face.Label,
                    edgeLength = face.EdgeLength * factor,
                    straightLength = face.StraightLength * factor
                }),
                corners = part.Corners.Select(corner => new
                {
                    point = Xy(ToPoint(corner.Point), factor),
                    outerRadius = corner.OuterRadius * factor,
                    innerRadius = corner.InnerRadius * factor
                }),
                elevations = elevations.Select(elevation => new
                {
                    faces = elevation.Labels,
                    length = elevation.Length * factor,
                    straightLength = elevation.StraightLength * factor
                }),
                perimeter = new
                {
                    outer = part.Outer.Length * factor,
                    inner = part.Inner.Length * factor,
                    centreline = part.Centre.Length * factor
                },
                extents = new { min = Xy(ToPoint(minimum), factor), max = Xy(ToPoint(maximum), factor) },
                layers = new { band = bandLayer, light = lightLayer, dimension = dimensionLayer, annotation = annotationLayer },
                outlineHandles = handles.Outline,
                fillHandles = handles.Fill,
                lightHandles = handles.Light,
                dimensionHandles = handles.Dimensions,
                annotationHandles = handles.Annotations
            }
        );
    }

    private static List<double> ReadCornerRadii(JsonElement arguments, int count, double scale)
    {
        if (HasValue(arguments, "cornerRadii"))
        {
            var array = arguments.GetProperty("cornerRadii");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != count)
            {
                throw new ArgumentException($"cornerRadii must contain exactly {count} radii, one per corner in points order.");
            }
            var radii = new List<double>(count);
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Number
                    || !element.TryGetDouble(out var value)
                    || !double.IsFinite(value)
                    || value < 0
                    || value > MaxSize)
                {
                    throw new ArgumentException("Each corner radius must be a number from 0 upward.");
                }
                radii.Add(ToDrawing(value, scale, "cornerRadii"));
            }
            return radii;
        }

        var radius = OptionalNonNegative(arguments, "cornerRadius", scale) ?? 0;
        return Enumerable.Repeat(radius, count).ToList();
    }

    // The light strip sits `offset` above the bottom edge of every elevation;
    // without an offset it is centred on the band height.
    private static LightStrip? ReadLightStrip(JsonElement arguments, double bandHeight, double scale)
    {
        if (!HasValue(arguments, "lightStrip"))
        {
            return null;
        }
        var strip = arguments.GetProperty("lightStrip");
        if (strip.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("lightStrip must be an object with height and an optional offset.");
        }
        double height;
        double offset;
        try
        {
            height = PositiveSize(strip, "height", scale);
            offset = OptionalNonNegative(strip, "offset", scale) ?? (bandHeight - height) / 2;
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"lightStrip.{exception.Message}");
        }
        if (height >= bandHeight || offset + height > bandHeight + Tolerance)
        {
            throw new ArgumentException("lightStrip must fit inside the band height: offset + height <= height.");
        }
        return new LightStrip(offset, height);
    }

    private static Color? OptionalColorIndex(JsonElement arguments, string name)
    {
        if (!HasValue(arguments, name))
        {
            return null;
        }
        var property = arguments.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var index) || index is < 1 or > 255)
        {
            throw new ArgumentException($"{name} must be an AutoCAD Color Index from 1 to 255.");
        }
        return Color.FromColorIndex(ColorMethod.ByAci, (short)index);
    }

    private static Polyline LoopPolyline(BandLoop loop)
    {
        var polyline = new Polyline();
        for (var index = 0; index < loop.Vertices.Count; index++)
        {
            polyline.AddVertexAt(index, new Point2d(loop.Vertices[index].X, loop.Vertices[index].Y), loop.Bulges[index], 0, 0);
        }
        polyline.Closed = true;
        return polyline;
    }

    // A non-associative solid fill of `outer`, leaving `hole` empty.
    private static string AddSolidHatch(
        BlockTableRecord modelSpace,
        Transaction transaction,
        string layerName,
        Color color,
        BandLoop outer,
        BandLoop? hole
    )
    {
        using var hatch = new Hatch();
        hatch.SetDatabaseDefaults();
        hatch.Layer = layerName;
        hatch.Color = color;
        modelSpace.AppendEntity(hatch);
        transaction.AddNewlyCreatedDBObject(hatch, true);
        hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
        hatch.Associative = false;
        hatch.AppendLoop(HatchLoopTypes.External | HatchLoopTypes.Polyline, LoopPoints(outer), LoopBulges(outer));
        if (hole is not null)
        {
            hatch.AppendLoop(HatchLoopTypes.Polyline, LoopPoints(hole), LoopBulges(hole));
        }
        hatch.EvaluateHatch(true);
        return hatch.Handle.ToString();
    }

    private static Point2dCollection LoopPoints(BandLoop loop)
    {
        var points = new Point2dCollection();
        foreach (var vertex in loop.Vertices)
        {
            points.Add(new Point2d(vertex.X, vertex.Y));
        }
        points.Add(new Point2d(loop.Vertices[0].X, loop.Vertices[0].Y));
        return points;
    }

    private static DoubleCollection LoopBulges(BandLoop loop)
    {
        var bulges = new DoubleCollection();
        foreach (var bulge in loop.Bulges)
        {
            bulges.Add(bulge);
        }
        bulges.Add(0);
        return bulges;
    }

    // Sizes the dimension to the sheet text height and makes it read in the
    // caller's units with one decimal, e.g. 130.0 in a millimetre drawing
    // dimensioned in cm. DIMSCALE is reset so the drawing's dimension style
    // cannot scale the sizes a second time.
    private static void StyleBandDimension(Dimension dimension, double textHeight, double unitScale)
    {
        dimension.Dimscale = 1;
        dimension.Dimtxt = textHeight * 0.8;
        dimension.Dimasz = textHeight * 0.6;
        dimension.Dimexe = textHeight * 0.4;
        dimension.Dimexo = textHeight * 0.3;
        dimension.Dimgap = textHeight * 0.25;
        dimension.Dimdec = 1;
        dimension.Dimzin = 0;
        dimension.Dimlfac = 1 / unitScale;
        dimension.Dimtad = 1;
        dimension.Dimtih = false;
        dimension.Dimtoh = false;
        dimension.RecomputeDimensionBlock(true);
    }
}
