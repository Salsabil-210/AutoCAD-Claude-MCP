using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADClaudeBridge;

// Production book assembly plan: every part's plan outline in its hanging
// position, named with callouts on the right, hanger points, and the overall
// size. Uses the same geometry as draw_band_part and draw_bent_bar_part, so
// the outlines match the part sheets exactly.
internal static partial class BridgeHost
{
    private const int MaxAssemblyParts = 30;
    private const int MaxHangers = 50;
    private static readonly string[] AssemblyPartTypes = { "band", "bent_bar" };

    private sealed record AssemblyPart(string Label, List<BandLoop> Loops, Vec Anchor, Vec Min, Vec Max, Autodesk.AutoCAD.Colors.Color? Fill);

    private static string DrawAssemblyPlan(JsonElement arguments)
    {
        var document = RequireDocument();
        var database = document.Database;
        var units = Units(arguments, database);
        var title = OptionalText(arguments, "title")?.Trim() is { Length: > 0 } given ? given : "PLAN";
        var parts = ReadAssemblyParts(arguments, units.Scale);
        var hangers = HasValue(arguments, "hangers") ? ReadPoints(arguments, "hangers", 1, units.Scale) : new List<Point2d>();
        if (hangers.Count > MaxHangers)
        {
            throw new ArgumentException($"hangers must contain at most {MaxHangers} points.");
        }
        var bandLayer = OptionalName(arguments, "bandLayer", "bandLayer") ?? BandLayerDefault;
        var dimensionLayer = OptionalName(arguments, "dimensionLayer", "dimensionLayer") ?? SignDimensionLayerDefault;
        var annotationLayer = OptionalName(arguments, "annotationLayer", "annotationLayer") ?? SignAnnotationLayerDefault;

        var min = new Vec(parts.Min(part => part.Min.X), parts.Min(part => part.Min.Y));
        var max = new Vec(parts.Max(part => part.Max.X), parts.Max(part => part.Max.Y));
        var s = OptionalPositive(arguments, "textHeight", units.Scale) ?? Math.Max(max.X - min.X, max.Y - min.Y) / 40;

        using var documentLock = document.LockDocument();
        using var transaction = database.TransactionManager.StartTransaction();
        foreach (var layerName in new[] { bandLayer, dimensionLayer, annotationLayer }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            EnsureLayer(database, transaction, layerName);
        }
        var sheet = new SheetWriter(database, transaction, s, units.Scale, dimensionLayer, annotationLayer);
        var outlineHandles = new List<string>();
        var dimensionHandles = new List<string>();
        var annotationHandles = new List<string>();

        foreach (var part in parts)
        {
            if (part.Fill is not null)
            {
                outlineHandles.Add(sheet.Fill(part.Loops[0], part.Loops.Count > 1 ? part.Loops[1] : null, bandLayer, part.Fill));
            }
            foreach (var loop in part.Loops)
            {
                outlineHandles.Add(sheet.Outline(loop, bandLayer));
            }
        }

        // Callouts stacked on the right, top to bottom in the order of each
        // part's anchor height, so leaders do not cross more than needed.
        var calloutX = max.X + 8 * s;
        var y = max.Y;
        foreach (var part in parts.OrderByDescending(part => part.Anchor.Y))
        {
            var label = new Vec(calloutX, y);
            annotationHandles.Add(sheet.Segment(part.Anchor, label - new Vec(0.5 * s, 0), annotationLayer));
            using (var dot = new Circle(ToPoint(part.Anchor), Vector3d.ZAxis, s * 0.25))
            {
                annotationHandles.Add(sheet.Add(dot, annotationLayer));
            }
            annotationHandles.Add(sheet.Note(label, s, part.Label.ToUpperInvariant(), AttachmentPoint.MiddleLeft));
            y -= 2.5 * s;
        }

        for (var index = 0; index < hangers.Count; index++)
        {
            var point = new Vec(hangers[index].X, hangers[index].Y);
            using (var ring = new Circle(ToPoint(point), Vector3d.ZAxis, s * 0.6))
            {
                annotationHandles.Add(sheet.Add(ring, annotationLayer));
            }
            annotationHandles.Add(sheet.Segment(point - new Vec(s, 0), point + new Vec(s, 0), annotationLayer));
            annotationHandles.Add(sheet.Segment(point - new Vec(0, s), point + new Vec(0, s), annotationLayer));
            annotationHandles.Add(sheet.Note(point + new Vec(0.9 * s, 0.9 * s), s * 0.7, $"H{index + 1}", AttachmentPoint.BottomLeft));
        }

        dimensionHandles.Add(sheet.Rotated(0, min, new Vec(max.X, min.Y), new Vec(min.X, min.Y - 4 * s)));
        dimensionHandles.Add(sheet.Rotated(Math.PI / 2, min, new Vec(min.X, max.Y), new Vec(min.X - 4 * s, min.Y)));
        var titleTop = max.Y + 4 * s;
        annotationHandles.Add(sheet.Note(new Vec(min.X - 6 * s, titleTop), s * 1.5, title.ToUpperInvariant(), AttachmentPoint.BottomLeft));
        transaction.Commit();

        var longestLabel = parts.Max(part => part.Label.Length);
        var minimum = new Vec(min.X - 8 * s, min.Y - 8 * s);
        var maximum = new Vec(calloutX + longestLabel * s * 0.8 + 2 * s, titleTop + 2 * s);
        var factor = 1 / units.Scale;
        document.Editor.WriteMessage($"\nClaude drew the assembly plan with {parts.Count} parts. Use UNDO to remove it.");
        return Success(
            $"Assembly plan drawn: {parts.Count} part(s), {hangers.Count} hanger point(s).",
            new
            {
                units = units.Label,
                textHeight = s * factor,
                overall = new { width = (max.X - min.X) * factor, depth = (max.Y - min.Y) * factor },
                parts = parts.Select(part => new
                {
                    label = part.Label,
                    min = Xy(ToPoint(part.Min), factor),
                    max = Xy(ToPoint(part.Max), factor)
                }),
                extents = new { min = Xy(ToPoint(minimum), factor), max = Xy(ToPoint(maximum), factor) },
                outlineHandles,
                dimensionHandles,
                annotationHandles
            }
        );
    }

    // Each part is placed with `at` added to its own coordinates, so the
    // spec's part geometry can be passed unchanged.
    private static List<AssemblyPart> ReadAssemblyParts(JsonElement arguments, double scale)
    {
        if (!HasValue(arguments, "parts") || arguments.GetProperty("parts").ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("parts must be an array.");
        }
        var array = arguments.GetProperty("parts");
        if (array.GetArrayLength() is < 1 or > MaxAssemblyParts)
        {
            throw new ArgumentException($"parts must contain 1 to {MaxAssemblyParts} parts.");
        }

        var parts = new List<AssemblyPart>();
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException($"parts[{index}] must be an object.");
            }
            try
            {
                var label = RequiredText(element, "label").Trim();
                var offset = HasValue(element, "at")
                    ? Nested(element, "at", at => Point(at, "x", "y", scale))
                    : Point3d.Origin;
                var shift = new Vec(offset.X, offset.Y);
                var thickness = PositiveSize(element, "thickness", scale);
                var fill = OptionalColorIndex(element, "fillColor");
                if (Choice(element, "type", AssemblyPartTypes, "band") == "band")
                {
                    var corners = ReadPolygon(element, "points", scale).Select(point => new Vec(point.X, point.Y) + shift).ToList();
                    var band = BandGeometry.Build(corners, ReadCornerRadii(element, corners.Count, scale), thickness, scale);
                    // Callout on the right-most face.
                    var face = band.Faces.MaxBy(candidate => candidate.Midpoint.X)!;
                    parts.Add(new AssemblyPart(
                        label,
                        new List<BandLoop> { band.Outer, band.Inner },
                        face.FlatMidpoint,
                        new Vec(corners.Min(point => point.X), corners.Min(point => point.Y)),
                        new Vec(corners.Max(point => point.X), corners.Max(point => point.Y)),
                        fill
                    ));
                }
                else
                {
                    var start = Nested(element, "start", point => Point(point, "x", "y", scale));
                    var bar = BentBarGeometry.Build(
                        new Vec(start.X, start.Y) + shift,
                        DegreesToRadians(OptionalAngle(element, "startAngle", 0)),
                        ReadBarSteps(element, scale),
                        thickness
                    );
                    // Callout on the middle of the middle piece, on the bar itself.
                    var middle = bar.Pieces[bar.Pieces.Count / 2];
                    var anchor = middle.Kind == BentBarGeometry.Arc
                        ? middle.Center + Rotate(middle.Start - middle.Center, middle.Sweep / 2)
                        : (middle.Start + middle.End) * 0.5;
                    parts.Add(new AssemblyPart(label, new List<BandLoop> { bar.Outline }, anchor, bar.Min, bar.Max, fill));
                }
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"parts[{index}]: {exception.Message}");
            }
            index++;
        }
        return parts;
    }
}
