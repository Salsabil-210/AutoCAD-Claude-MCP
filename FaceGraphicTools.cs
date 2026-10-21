using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCADClaudeBridge;

// Ceiling feature production drawings: letters or chevrons on one band face,
// drawn onto an elevation that draw_band_part already drew. The elevation is
// passed back exactly as draw_band_part returned it (x, y of its bottom-left
// corner, length, height), so this tool keeps no state of its own.
internal static partial class BridgeHost
{
    private const short DefaultGraphicColor = 7;
    private static readonly string[] GraphicKinds = { "letters", "chevrons" };
    private static readonly string[] LetterFinishes = { "cut_through", "engraved", "applied" };
    private static readonly string[] Pointing = { "right", "left" };

    private sealed record FaceElevation(Vec Origin, double Length, double Height);

    private static string AddFaceGraphic(JsonElement arguments)
    {
        var document = RequireDocument();
        var database = document.Database;
        var units = Units(arguments, database);
        var face = Nested(arguments, "elevation", element => new FaceElevation(
            new Vec(Length(element, "x", units.Scale), Length(element, "y", units.Scale)),
            PositiveSize(element, "length", units.Scale),
            PositiveSize(element, "height", units.Scale)
        ));
        var kind = Choice(arguments, "kind", GraphicKinds, "letters");
        var color = OptionalColorIndex(arguments, "color") ?? Color.FromColorIndex(ColorMethod.ByAci, DefaultGraphicColor);
        var graphicLayer = OptionalName(arguments, "graphicLayer", "graphicLayer") ?? GraphicLayerDefault;
        var dimensionLayer = OptionalName(arguments, "dimensionLayer", "dimensionLayer") ?? SignDimensionLayerDefault;
        var annotationLayer = OptionalName(arguments, "annotationLayer", "annotationLayer") ?? SignAnnotationLayerDefault;
        var s = OptionalPositive(arguments, "textHeight", units.Scale) ?? face.Length / 30;

        using var documentLock = document.LockDocument();
        using var transaction = database.TransactionManager.StartTransaction();
        foreach (var layerName in new[] { graphicLayer, dimensionLayer, annotationLayer }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            EnsureLayer(database, transaction, layerName);
        }
        var sheet = new SheetWriter(database, transaction, s, units.Scale, dimensionLayer, annotationLayer);
        var left = face.Origin.X;
        var right = left + face.Length;
        var bottom = face.Origin.Y;
        var top = bottom + face.Height;
        var graphicHandles = new List<string>();
        var dimensionHandles = new List<string>();
        var annotationHandles = new List<string>();
        double start;
        double end;
        double graphicBottom;
        double graphicTop;
        string note;

        if (kind == "letters")
        {
            var text = RequiredText(arguments, "text").Trim();
            var letterHeight = PositiveSize(arguments, "letterHeight", units.Scale);
            var finish = Choice(arguments, "finish", LetterFinishes, "cut_through");
            if (letterHeight > face.Height + Tolerance)
            {
                throw new ArgumentException($"letterHeight {sheet.Format(letterHeight)} is taller than the band ({sheet.Format(face.Height)}).");
            }
            var at = OptionalNonNegative(arguments, "at", units.Scale);
            var middle = bottom + face.Height / 2;
            var (handle, min, max) = at is double offset
                ? sheet.Text(new Vec(left + offset, middle), letterHeight, text, AttachmentPoint.MiddleLeft, graphicLayer, color)
                : sheet.Text(new Vec((left + right) / 2, middle), letterHeight, text, AttachmentPoint.MiddleCenter, graphicLayer, color);
            if (min.X < left - Tolerance || max.X > right + Tolerance || min.Y < bottom - Tolerance || max.Y > top + Tolerance)
            {
                // Not committed: the transaction is rolled back and nothing is drawn.
                throw new ArgumentException(
                    $"\"{text}\" at {sheet.Format(letterHeight)} high runs from {sheet.Format(min.X - left)} to {sheet.Format(max.X - left)} along the face, "
                    + $"outside the face (0 to {sheet.Format(face.Length)}) or the band height. Use smaller letters or a different at."
                );
            }
            graphicHandles.Add(handle);
            (start, end, graphicBottom, graphicTop) = (min.X, max.X, bottom, top);
            note = $"LETTERS \"{text.ToUpperInvariant()}\" {sheet.Format(letterHeight)} H, {finish.Replace('_', ' ').ToUpperInvariant()}";
        }
        else
        {
            var at = OptionalNonNegative(arguments, "at", units.Scale)
                ?? throw new ArgumentException("at is required for chevrons: the distance from the face's left end to the first chevron.");
            var count = OptionalInteger(arguments, "count", 2, 1, FaceGraphicGeometry.MaxChevrons);
            var stroke = PositiveSize(arguments, "stroke", units.Scale);
            var chevronHeight = OptionalPositive(arguments, "chevronHeight", units.Scale) ?? face.Height;
            var depth = OptionalNonNegative(arguments, "depth", units.Scale) ?? chevronHeight / 2;
            var pitch = OptionalPositive(arguments, "pitch", units.Scale) ?? 2 * stroke;
            var pointRight = Choice(arguments, "pointing", Pointing, "right") == "right";
            var row = FaceGraphicGeometry.Chevrons(face.Length, face.Height, at, count, stroke, pitch, depth, chevronHeight, pointRight, units.Scale);

            foreach (var chevron in row.Chevrons)
            {
                var outline = new BandLoop(
                    chevron.Outline.ConvertAll(point => point + face.Origin),
                    chevron.Outline.ConvertAll(_ => 0.0),
                    0
                );
                graphicHandles.Add(sheet.Fill(outline, null, graphicLayer, color));
                graphicHandles.Add(sheet.Outline(outline, graphicLayer));
            }
            (start, end, graphicBottom, graphicTop) = (left + row.Start, left + row.End, bottom + row.Bottom, bottom + row.Top);
            if (row.Bottom < -Tolerance || row.Top > face.Height + Tolerance)
            {
                dimensionHandles.Add(sheet.Rotated(Math.PI / 2, new Vec(start, graphicBottom), new Vec(start, graphicTop), new Vec(start - 2 * s, graphicBottom)));
            }
            var proud = (chevronHeight - face.Height) / 2;
            note = $"{count} CHEVRON{(count > 1 ? "S" : "")} POINTING {(pointRight ? "RIGHT" : "LEFT")}, STROKE {sheet.Format(stroke)}, "
                + $"PITCH {sheet.Format(pitch)}, DEPTH {sheet.Format(depth)}, {sheet.Format(chevronHeight)} H"
                + (proud > Tolerance ? $" ({sheet.Format(proud)} PROUD TOP AND BOTTOM)" : string.Empty);
        }

        // Position along the face, chained above the elevation.
        var dimensionLine = Math.Max(top, graphicTop) + 1.5 * s;
        foreach (var (from, to) in new[] { (left, start), (start, end), (end, right) })
        {
            if (to - from > Tolerance)
            {
                dimensionHandles.Add(sheet.Rotated(0, new Vec(from, top), new Vec(to, top), new Vec(from, dimensionLine)));
            }
        }
        annotationHandles.Add(sheet.Note(new Vec(left, Math.Min(bottom, graphicBottom) - 9 * s), s * 0.8, note, AttachmentPoint.TopLeft));

        transaction.Commit();

        var factor = 1 / units.Scale;
        document.Editor.WriteMessage($"\nClaude added {kind} to a band face. Use UNDO to remove them.");
        return Success(
            $"Face graphic ({kind}) added.",
            new
            {
                kind,
                units = units.Label,
                start = (start - left) * factor,
                end = (end - left) * factor,
                bottom = (graphicBottom - bottom) * factor,
                top = (graphicTop - bottom) * factor,
                note,
                layers = new { graphic = graphicLayer, dimension = dimensionLayer, annotation = annotationLayer },
                graphicHandles,
                dimensionHandles,
                annotationHandles
            }
        );
    }
}
