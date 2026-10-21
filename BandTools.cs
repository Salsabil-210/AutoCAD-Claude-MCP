using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCADClaudeBridge;

// Ceiling feature production drawings: a band part (a closed ring of sheet
// material standing on edge) drawn as a production sheet section: plan with
// face letters and dimensions, plus one elevation per face or per group of
// identical faces, with an optional LED light strip.
internal static partial class BridgeHost
{
    private const short DefaultLightColor = 2;
    // Faces whose lengths agree within this many user units share an elevation.
    private const double FaceGroupTolerance = 0.05;

    private sealed record LightStrip(double Offset, double Height);

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

        var part = BandGeometry.Build(corners, radii, thickness, units.Scale);
        var elevations = BandGeometry.GroupFaces(part.Faces, groupFaces, FaceGroupTolerance * units.Scale);

        var planMin = new Vec(corners.Min(point => point.X), corners.Min(point => point.Y));
        var planMax = new Vec(corners.Max(point => point.X), corners.Max(point => point.Y));
        var extent = Math.Max(Math.Max(planMax.X - planMin.X, planMax.Y - planMin.Y), elevations.Max(elevation => elevation.Length));
        var s = OptionalPositive(arguments, "textHeight", units.Scale) ?? extent / 40;

        using var documentLock = document.LockDocument();
        using var transaction = database.TransactionManager.StartTransaction();
        foreach (var layerName in new[] { bandLayer, lightLayer, dimensionLayer, annotationLayer }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            EnsureLayer(database, transaction, layerName);
        }
        var sheet = new SheetWriter(database, transaction, s, units.Scale, dimensionLayer, annotationLayer);
        var outlineHandles = new List<string>();
        var fillHandles = new List<string>();
        var lightHandles = new List<string>();
        var dimensionHandles = new List<string>();
        var annotationHandles = new List<string>();

        // Plan ---------------------------------------------------------------
        outlineHandles.Add(sheet.Outline(part.Outer, bandLayer));
        outlineHandles.Add(sheet.Outline(part.Inner, bandLayer));
        if (fillColor is not null)
        {
            fillHandles.Add(sheet.Fill(part.Outer, part.Inner, bandLayer, fillColor));
        }

        foreach (var face in part.Faces)
        {
            dimensionHandles.Add(sheet.Aligned(face.Start, face.End, face.Midpoint + face.Outward * (3 * s)));
            var markerTip = face.Midpoint + face.Outward * (5.5 * s);
            annotationHandles.Add(sheet.Marker(markerTip, face.Outward * -1));
            annotationHandles.Add(sheet.Note(markerTip + face.Outward * (2.2 * s), s, face.Label, AttachmentPoint.MiddleCenter));
        }

        // Band thickness, measured on the flat part of face A.
        var firstFace = part.Faces[0];
        var faceDirection = (firstFace.End - firstFace.Start) * (1 / firstFace.EdgeLength);
        dimensionHandles.Add(sheet.Aligned(
            firstFace.FlatMidpoint,
            firstFace.FlatMidpoint - firstFace.Outward * thickness,
            firstFace.FlatMidpoint + faceDirection * (2 * s)
        ));

        // One radius note per distinct pair of outer and inner radii.
        var labelled = new HashSet<string>();
        foreach (var corner in part.Corners.Where(corner => corner.OuterRadius > 0))
        {
            var note = corner.InnerRadius > 0
                ? $"R{sheet.Format(corner.OuterRadius)} OUT / R{sheet.Format(corner.InnerRadius)} IN"
                : $"R{sheet.Format(corner.OuterRadius)} OUT / SHARP IN";
            if (labelled.Add(note))
            {
                var attachment = corner.Outward.X >= 0 ? AttachmentPoint.MiddleLeft : AttachmentPoint.MiddleRight;
                annotationHandles.Add(sheet.Note(corner.Point + corner.Outward * (1.5 * s), s * 0.8, note, attachment));
            }
        }

        var titleTop = planMax.Y + 10 * s;
        annotationHandles.Add(sheet.Note(new Vec(planMin.X - 8 * s, titleTop), s * 1.5, $"{name.ToUpperInvariant()}    x{quantity}", AttachmentPoint.BottomLeft));

        var summary = new List<string>
        {
            $"BAND {sheet.Format(thickness)} THK x {sheet.Format(height)} H",
            $"DEVELOPED LENGTH (CENTRELINE) {sheet.Format(part.Centre.Length)}",
            $"OUTER {sheet.Format(part.Outer.Length)} / INNER {sheet.Format(part.Inner.Length)}"
        };
        if (light is not null)
        {
            summary.Add($"LED STRIP {sheet.Format(light.Height)} AT {sheet.Format(light.Offset)} FROM BOTTOM");
        }
        annotationHandles.Add(sheet.Note(new Vec(planMin.X - 8 * s, planMin.Y - 10 * s), s * 0.8, string.Join("\n", summary), AttachmentPoint.TopLeft));

        // Elevations ---------------------------------------------------------
        // Each elevation leaves 5 text heights above the band for its title
        // and for face graphics that stand proud of the band (add_face_graphic).
        var left = planMax.X + 16 * s;
        var cursor = titleTop;
        var elevationRight = left;
        var elevationOrigins = new List<object>();
        foreach (var elevation in elevations)
        {
            var labels = string.Join(",", elevation.Labels);
            annotationHandles.Add(sheet.Note(new Vec(left, cursor), s * 1.2, $"ELEVATION \"{labels}\"", AttachmentPoint.BottomLeft));
            var top = cursor - 5 * s;
            var bottom = top - height;
            var right = left + elevation.Length;
            elevationRight = Math.Max(elevationRight, right + 10 * s);

            var rectangle = Rectangle(new Vec(left, bottom), new Vec(right, top));
            outlineHandles.Add(sheet.Outline(rectangle, bandLayer));
            if (fillColor is not null)
            {
                fillHandles.Add(sheet.Fill(rectangle, null, bandLayer, fillColor));
            }
            dimensionHandles.Add(sheet.Rotated(0, new Vec(left, bottom), new Vec(right, bottom), new Vec(left, bottom - 3 * s)));
            dimensionHandles.Add(sheet.Rotated(Math.PI / 2, new Vec(right, bottom), new Vec(right, top), new Vec(right + 6 * s, bottom)));

            if (light is not null)
            {
                var stripBottom = bottom + light.Offset;
                var stripTop = stripBottom + light.Height;
                lightHandles.Add(sheet.Fill(Rectangle(new Vec(left, stripBottom), new Vec(right, stripTop)), null, lightLayer, lightColor));
                lightHandles.Add(sheet.Segment(new Vec(left, stripBottom), new Vec(right, stripBottom), lightLayer));
                lightHandles.Add(sheet.Segment(new Vec(left, stripTop), new Vec(right, stripTop), lightLayer));
                foreach (var (from, to) in new[] { (bottom, stripBottom), (stripBottom, stripTop), (stripTop, top) })
                {
                    if (to - from > Tolerance)
                    {
                        dimensionHandles.Add(sheet.Rotated(Math.PI / 2, new Vec(right, from), new Vec(right, to), new Vec(right + 3 * s, from)));
                    }
                }
            }

            var below = bottom - 6 * s;
            if (Math.Abs(elevation.StraightLength - elevation.Length) > FaceGroupTolerance * units.Scale)
            {
                annotationHandles.Add(sheet.Note(new Vec(right, below), s * 0.8, $"FLAT {sheet.Format(elevation.StraightLength)} + CORNER RADII", AttachmentPoint.TopRight));
            }
            var markerTip = new Vec((left + right) / 2, below + s * 0.5);
            annotationHandles.Add(sheet.Marker(markerTip, new Vec(0, 1)));
            annotationHandles.Add(sheet.Note(markerTip - new Vec(0, 1.6 * s), s, labels, AttachmentPoint.TopCenter));

            elevationOrigins.Add(new
            {
                faces = elevation.Labels,
                x = left / units.Scale,
                y = bottom / units.Scale,
                length = elevation.Length / units.Scale,
                height = height / units.Scale
            });
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
                textHeight = s * factor,
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
                elevations = elevationOrigins,
                perimeter = new
                {
                    outer = part.Outer.Length * factor,
                    inner = part.Inner.Length * factor,
                    centreline = part.Centre.Length * factor
                },
                extents = new { min = Xy(ToPoint(minimum), factor), max = Xy(ToPoint(maximum), factor) },
                layers = new { band = bandLayer, light = lightLayer, dimension = dimensionLayer, annotation = annotationLayer },
                outlineHandles,
                fillHandles,
                lightHandles,
                dimensionHandles,
                annotationHandles
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
        var (height, offset) = Nested(arguments, "lightStrip", strip =>
        {
            var stripHeight = PositiveSize(strip, "height", scale);
            return (stripHeight, OptionalNonNegative(strip, "offset", scale) ?? (bandHeight - stripHeight) / 2);
        });
        if (height >= bandHeight || offset + height > bandHeight + Tolerance)
        {
            throw new ArgumentException("lightStrip must fit inside the band height: offset + height <= height.");
        }
        return new LightStrip(offset, height);
    }
}
