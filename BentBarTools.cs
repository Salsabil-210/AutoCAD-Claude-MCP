using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCADClaudeBridge;

// Ceiling feature production drawings: a bent bar part (a strip of sheet on
// edge following straight runs and arcs in plan, such as a decorative arrow)
// drawn as plan plus developed elevation with bend positions and arrowheads.
internal static partial class BridgeHost
{
    private static readonly string[] BarEnds = { BentBarGeometry.Plain, BentBarGeometry.Arrow };
    private static readonly string[] BarStepKinds = { BentBarGeometry.Line, BentBarGeometry.Arc };
    private static readonly string[] Turns = { "left", "right" };

    private static string DrawBentBarPart(JsonElement arguments)
    {
        var document = RequireDocument();
        var database = document.Database;
        var units = Units(arguments, database);
        var name = RequiredText(arguments, "name").Trim();
        var quantity = OptionalInteger(arguments, "quantity", 1, 1, 10_000);
        var start = Nested(arguments, "start", element => Point(element, "x", "y", units.Scale));
        var startAngle = DegreesToRadians(OptionalAngle(arguments, "startAngle", 0));
        var steps = ReadBarSteps(arguments, units.Scale);
        var thickness = PositiveSize(arguments, "thickness", units.Scale);
        var height = PositiveSize(arguments, "height", units.Scale);
        var (startEnd, endEnd) = HasValue(arguments, "ends")
            ? Nested(arguments, "ends", ends => (
                Choice(ends, "start", BarEnds, BentBarGeometry.Plain),
                Choice(ends, "end", BarEnds, BentBarGeometry.Plain)))
            : (BentBarGeometry.Plain, BentBarGeometry.Plain);
        var hasArrow = startEnd == BentBarGeometry.Arrow || endEnd == BentBarGeometry.Arrow;
        var arrowHeight = OptionalPositive(arguments, "arrowHeight", units.Scale) ?? 0;
        if (hasArrow && arrowHeight <= 0)
        {
            throw new ArgumentException("arrowHeight is required when an end is an arrow.");
        }
        var fillColor = OptionalColorIndex(arguments, "fillColor");
        var barLayer = OptionalName(arguments, "barLayer", "barLayer") ?? BandLayerDefault;
        var dimensionLayer = OptionalName(arguments, "dimensionLayer", "dimensionLayer") ?? SignDimensionLayerDefault;
        var annotationLayer = OptionalName(arguments, "annotationLayer", "annotationLayer") ?? SignAnnotationLayerDefault;

        var bar = BentBarGeometry.Build(new Vec(start.X, start.Y), startAngle, steps, thickness);
        var developed = BentBarGeometry.Elevation(bar.Length, height, startEnd, endEnd, arrowHeight, units.Scale);
        var extent = Math.Max(Math.Max(bar.Max.X - bar.Min.X, bar.Max.Y - bar.Min.Y), bar.Length);
        var s = OptionalPositive(arguments, "textHeight", units.Scale) ?? extent / 40;

        using var documentLock = document.LockDocument();
        using var transaction = database.TransactionManager.StartTransaction();
        foreach (var layerName in new[] { barLayer, dimensionLayer, annotationLayer }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            EnsureLayer(database, transaction, layerName);
        }
        var sheet = new SheetWriter(database, transaction, s, units.Scale, dimensionLayer, annotationLayer);
        var outlineHandles = new List<string>();
        var fillHandles = new List<string>();
        var dimensionHandles = new List<string>();
        var annotationHandles = new List<string>();

        // Plan ---------------------------------------------------------------
        outlineHandles.Add(sheet.Outline(bar.Outline, barLayer));
        if (fillColor is not null)
        {
            fillHandles.Add(sheet.Fill(bar.Outline, null, barLayer, fillColor));
        }

        // Run dimensions go on the outside of the overall turn.
        var outside = bar.Pieces.Sum(piece => piece.Sweep) >= 0 ? -1 : 1;
        var bendNumber = 0;
        var bendNotes = new List<string>();
        foreach (var piece in bar.Pieces)
        {
            if (piece.Kind == BentBarGeometry.Line)
            {
                var side = new Vec(-piece.StartDirection.Y, piece.StartDirection.X) * outside;
                dimensionHandles.Add(sheet.Aligned(piece.Start, piece.End, (piece.Start + piece.End) * 0.5 + side * (thickness / 2 + 3 * s)));
                continue;
            }

            bendNumber++;
            var middle = piece.Center + Rotate(piece.Start - piece.Center, piece.Sweep / 2);
            var away = (middle - piece.Center) * (1 / piece.Radius);
            var degrees = RadiansToDegrees(Math.Abs(piece.Sweep)).ToString("0.0", CultureInfo.InvariantCulture);
            var turn = piece.Sweep > 0 ? "LEFT" : "RIGHT";
            annotationHandles.Add(sheet.Note(
                middle + away * (thickness / 2 + 2 * s),
                s * 0.8,
                $"BEND {bendNumber}: R{sheet.Format(piece.Radius)} {degrees}° {turn}\nARC {sheet.Format(piece.Length)} (CL)",
                away.X >= 0 ? AttachmentPoint.MiddleLeft : AttachmentPoint.MiddleRight
            ));
            bendNotes.Add($"BEND {bendNumber} AT {sheet.Format(piece.StartDistance)}: R{sheet.Format(piece.Radius)} {degrees}° {turn}, ARC {sheet.Format(piece.Length)}");
        }

        var first = bar.Pieces[0];
        var last = bar.Pieces[^1];
        annotationHandles.Add(sheet.Note(first.Start - first.StartDirection * (1.5 * s), s * 0.8, "START", AttachmentPoint.MiddleCenter));
        annotationHandles.Add(sheet.Note(last.End + last.EndDirection * (1.5 * s), s * 0.8, "END", AttachmentPoint.MiddleCenter));

        dimensionHandles.Add(sheet.Rotated(0, bar.Min, new Vec(bar.Max.X, bar.Min.Y), new Vec(bar.Min.X, bar.Min.Y - 7 * s)));
        dimensionHandles.Add(sheet.Rotated(Math.PI / 2, bar.Min, new Vec(bar.Min.X, bar.Max.Y), new Vec(bar.Min.X - 7 * s, bar.Min.Y)));

        var titleTop = bar.Max.Y + 10 * s;
        annotationHandles.Add(sheet.Note(new Vec(bar.Min.X - 8 * s, titleTop), s * 1.5, $"{name.ToUpperInvariant()}    x{quantity}", AttachmentPoint.BottomLeft));

        string EndText(string end) => end == BentBarGeometry.Arrow ? $"ARROW {sheet.Format(arrowHeight)} H" : "PLAIN";
        var summary = new List<string>
        {
            $"BAR {sheet.Format(thickness)} THK x {sheet.Format(height)} H",
            $"DEVELOPED LENGTH (CENTRELINE) {sheet.Format(bar.Length)}",
            $"START {EndText(startEnd)} / END {EndText(endEnd)}"
        };
        summary.AddRange(bendNotes);
        annotationHandles.Add(sheet.Note(new Vec(bar.Min.X - 8 * s, bar.Min.Y - 11 * s), s * 0.8, string.Join("\n", summary), AttachmentPoint.TopLeft));

        // Developed elevation -----------------------------------------------
        var left = bar.Max.X + 16 * s;
        annotationHandles.Add(sheet.Note(new Vec(left, titleTop), s * 1.2, "DEVELOPED ELEVATION (START LEFT)", AttachmentPoint.BottomLeft));
        var origin = new Vec(left, titleTop - 5 * s - developed.Max.Y);
        var outline = Translate(developed.Outline, origin);
        outlineHandles.Add(sheet.Outline(outline, barLayer));
        if (fillColor is not null)
        {
            fillHandles.Add(sheet.Fill(outline, null, barLayer, fillColor));
        }

        var shaftBottom = origin.Y;
        var shaftTop = origin.Y + height;
        var lowest = origin.Y + developed.Min.Y;
        bendNumber = 0;
        var boundaries = new List<double> { 0 };
        foreach (var piece in bar.Pieces)
        {
            boundaries.Add(piece.StartDistance + piece.Length);
            if (piece.Kind != BentBarGeometry.Arc)
            {
                continue;
            }
            bendNumber++;
            foreach (var distance in new[] { piece.StartDistance, piece.StartDistance + piece.Length })
            {
                annotationHandles.Add(sheet.Segment(new Vec(left + distance, shaftBottom), new Vec(left + distance, shaftTop), annotationLayer));
            }
            annotationHandles.Add(sheet.Note(
                new Vec(left + piece.StartDistance + piece.Length / 2, shaftTop + 0.5 * s),
                s * 0.7,
                $"BEND {bendNumber}",
                AttachmentPoint.BottomCenter
            ));
        }
        for (var index = 1; index < boundaries.Count; index++)
        {
            dimensionHandles.Add(sheet.Rotated(0,
                new Vec(left + boundaries[index - 1], shaftBottom),
                new Vec(left + boundaries[index], shaftBottom),
                new Vec(left, lowest - 3 * s)));
        }
        dimensionHandles.Add(sheet.Rotated(0, new Vec(left, shaftBottom), new Vec(left + bar.Length, shaftBottom), new Vec(left, lowest - 6 * s)));
        dimensionHandles.Add(sheet.Rotated(Math.PI / 2, new Vec(left, shaftBottom), new Vec(left, shaftTop), new Vec(left - 3 * s, shaftBottom)));

        var centreY = shaftBottom + height / 2;
        if (endEnd == BentBarGeometry.Arrow)
        {
            var x = left + bar.Length - arrowHeight / 2;
            dimensionHandles.Add(sheet.Rotated(Math.PI / 2, new Vec(x, centreY - arrowHeight / 2), new Vec(x, centreY + arrowHeight / 2), new Vec(left + bar.Length + 3 * s, centreY)));
        }
        if (startEnd == BentBarGeometry.Arrow)
        {
            var x = left + arrowHeight / 2;
            dimensionHandles.Add(sheet.Rotated(Math.PI / 2, new Vec(x, centreY - arrowHeight / 2), new Vec(x, centreY + arrowHeight / 2), new Vec(left - 6 * s, centreY)));
        }

        transaction.Commit();

        var minimum = new Vec(Math.Min(bar.Min.X - 8 * s, left - 8 * s), Math.Min(bar.Min.Y - 18 * s, lowest - 8 * s));
        var maximum = new Vec(left + bar.Length + 8 * s, titleTop + 2 * s);
        var factor = 1 / units.Scale;
        document.Editor.WriteMessage($"\nClaude drew bent bar part \"{name}\". Use UNDO to remove it.");
        return Success(
            $"Bent bar part \"{name}\" drawn: plan and developed elevation, {(bar.Length * factor).ToString("0.0", CultureInfo.InvariantCulture)} {units.Label} developed.",
            new
            {
                name,
                quantity,
                units = units.Label,
                thickness = thickness * factor,
                height = height * factor,
                textHeight = s * factor,
                developedLength = bar.Length * factor,
                ends = new { start = startEnd, end = endEnd, arrowHeight = hasArrow ? arrowHeight * factor : (double?)null },
                pieces = bar.Pieces.Select(piece => new
                {
                    kind = piece.Kind,
                    length = piece.Length * factor,
                    radius = piece.Kind == BentBarGeometry.Arc ? piece.Radius * factor : (double?)null,
                    angle = piece.Kind == BentBarGeometry.Arc ? RadiansToDegrees(piece.Sweep) : (double?)null,
                    startDistance = piece.StartDistance * factor,
                    start = Xy(ToPoint(piece.Start), factor),
                    end = Xy(ToPoint(piece.End), factor)
                }),
                planBounds = new { min = Xy(ToPoint(bar.Min), factor), max = Xy(ToPoint(bar.Max), factor) },
                elevation = new { x = left * factor, y = shaftBottom * factor, length = bar.Length * factor, height = height * factor },
                extents = new { min = Xy(ToPoint(minimum), factor), max = Xy(ToPoint(maximum), factor) },
                layers = new { bar = barLayer, dimension = dimensionLayer, annotation = annotationLayer },
                outlineHandles,
                fillHandles,
                dimensionHandles,
                annotationHandles
            }
        );
    }

    // Each step is a line (length) or an arc (radius, turn, and either angle
    // in degrees or length along the centreline).
    private static List<BarStep> ReadBarSteps(JsonElement arguments, double scale)
    {
        if (!HasValue(arguments, "segments") || arguments.GetProperty("segments").ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("segments must be an array of path steps.");
        }
        var array = arguments.GetProperty("segments");
        if (array.GetArrayLength() is < 1 or > BentBarGeometry.MaxSteps)
        {
            throw new ArgumentException($"segments must contain 1 to {BentBarGeometry.MaxSteps} steps.");
        }

        var steps = new List<BarStep>();
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var label = $"segments[{index}]";
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException($"{label} must be an object.");
            }
            try
            {
                var kind = Choice(element, "kind", BarStepKinds, BentBarGeometry.Line);
                if (kind == BentBarGeometry.Line)
                {
                    steps.Add(new BarStep(kind, PositiveSize(element, "length", scale), 0, 0));
                }
                else
                {
                    var radius = PositiveSize(element, "radius", scale);
                    var hasAngle = HasValue(element, "angle");
                    if (hasAngle == HasValue(element, "length"))
                    {
                        throw new ArgumentException("an arc needs exactly one of angle or length.");
                    }
                    var sweep = hasAngle
                        ? DegreesToRadians(PositiveNumber(element, "angle"))
                        : PositiveSize(element, "length", scale) / radius;
                    var sign = Choice(element, "turn", Turns, "left") == "left" ? 1 : -1;
                    steps.Add(new BarStep(kind, 0, radius, sign * sweep));
                }
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"{label}: {exception.Message}");
            }
            index++;
        }
        return steps;
    }

    private static Vec Rotate(Vec vector, double angle) => new(
        vector.X * Math.Cos(angle) - vector.Y * Math.Sin(angle),
        vector.X * Math.Sin(angle) + vector.Y * Math.Cos(angle)
    );
}
