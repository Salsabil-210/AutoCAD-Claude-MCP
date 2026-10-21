using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADClaudeBridge;

// Production book sheets: a Trust-Lines frame, title block and material
// legend drawn in model space around a part that is already drawn, scaled so
// the part fits a landscape A3 (or A4) sheet. Each sheet is saved in the DWG
// (CLAUDE_BRIDGE_SHEETS) so plot_sheets_pdf can plot the book in order.
// Also check_production_book, which checks a part spec without drawing.
internal static partial class BridgeHost
{
    private const string SheetsKey = "CLAUDE_BRIDGE_SHEETS";
    private const string SheetsMetaKey = "META";
    private const string SheetLayerDefault = "SIGN-SHEET";
    private const string LogoBlockDefault = "TRUSTLINES_LOGO";
    private const int MaxLegendRows = 8;
    private static readonly string[] Papers = { "A3", "A4" };
    private static readonly string[] SignOffLabels = { "ÜRETİM KOORDİNATÖRÜ", "ÜRETİM SORUMLUSU", "PROJE YÖNETİCİSİ" };
    // Title text on the dark bar prints white; ACI 7 would print black.
    private static readonly Color BarTextColor = Color.FromColorIndex(ColorMethod.ByAci, 255);
    private static readonly Color BarColor = Color.FromColorIndex(ColorMethod.ByAci, 250);
    private static readonly Color DraftColor = Color.FromColorIndex(ColorMethod.ByAci, 1);

    internal sealed record SheetRecord(
        string Id,
        string Name,
        int Number,
        string Paper,
        double MinX,
        double MinY,
        double MaxX,
        double MaxY,
        string FrameHandle,
        string CreatedAt
    );

    private sealed record LegendRow(string Name, string Ral, Color? Swatch);

    private static string DrawSheet(JsonElement arguments)
    {
        var document = RequireDocument();
        var database = document.Database;
        var units = Units(arguments, database);
        var (contentMin, contentMax) = Nested(arguments, "extents", extents => (
            Nested(extents, "min", min => Point(min, "x", "y", units.Scale)),
            Nested(extents, "max", max => Point(max, "x", "y", units.Scale))
        ));
        if (contentMax.X - contentMin.X <= Tolerance || contentMax.Y - contentMin.Y <= Tolerance)
        {
            throw new ArgumentException("extents.max must be above and to the right of extents.min.");
        }
        var job = Nested(arguments, "job", element => new
        {
            Number = OptionalText(element, "number") ?? string.Empty,
            Site = OptionalText(element, "site") ?? string.Empty,
            Title = OptionalText(element, "title") ?? string.Empty,
            Date = OptionalText(element, "date") ?? string.Empty
        });
        var sheetName = RequiredText(arguments, "sheetName").Trim();
        var number = OptionalInteger(arguments, "sheetNumber", 1, 1, 999);
        var count = OptionalInteger(arguments, "sheetCount", number, number, 999);
        var paper = Choice(arguments, "paper", Papers, "A3");
        var draft = OptionalBoolean(arguments, "draft", false);
        var hanger = OptionalBoolean(arguments, "hanger", true);
        var legend = ReadLegend(arguments);
        var layerName = OptionalName(arguments, "sheetLayer", "sheetLayer") ?? SheetLayerDefault;
        var logo = OptionalName(arguments, "logoBlock", "logoBlock") ?? LogoBlockDefault;

        // Laid out in A3 millimetres; A4 has the same proportions, so it is the
        // same layout plotted smaller.
        var paperMillimetres = paper == "A3" ? 1.0 : 297.0 / 420.0;
        var hasLegend = legend.Count > 0 || hanger;
        var legendRows = legend.Count + (hanger ? 1 : 0);
        var areaMin = new Vec(15, 47);
        var areaMax = new Vec(hasLegend ? 345 : 405, draft ? 275 : 282);
        var margin = 0.05 * Math.Max(contentMax.X - contentMin.X, contentMax.Y - contentMin.Y);
        var width = contentMax.X - contentMin.X + 2 * margin;
        var height = contentMax.Y - contentMin.Y + 2 * margin;
        // Drawing units per A3 millimetre.
        var k = Math.Max(width / (areaMax.X - areaMin.X), height / (areaMax.Y - areaMin.Y));
        var contentCentre = new Vec((contentMin.X + contentMax.X) / 2, (contentMin.Y + contentMax.Y) / 2);
        var origin = contentCentre - (areaMin + areaMax) * (0.5 * k);
        Vec At(double x, double y) => origin + new Vec(x, y) * k;
        double Size(double millimetres) => millimetres * k;
        // Drawing units per real paper millimetre, for the 1:n scale report.
        var plotScale = database.Insunits == UnitsValue.Undefined
            ? (double?)null
            : k / paperMillimetres / UnitsConverter.GetConversionFactor(UnitsValue.Millimeters, database.Insunits);

        using var documentLock = document.LockDocument();
        ImportLibraryBlock(database, logo);
        using var transaction = database.TransactionManager.StartTransaction();
        EnsureLayer(database, transaction, layerName);
        var sheet = new SheetWriter(database, transaction, Size(2.5), units.Scale, layerName, layerName);
        var handles = new List<string>();
        void Box(double x0, double y0, double x1, double y1) => handles.Add(sheet.Outline(Rectangle(At(x0, y0), At(x1, y1)), layerName));
        void Label(double x, double y, double size, string text, AttachmentPoint attachment, Color? color = null) =>
            handles.Add(sheet.Text(At(x, y), Size(size), text, attachment, layerName, color).Handle);

        // Frame and title strip.
        var frame = sheet.Outline(Rectangle(At(0, 0), At(420, 297)), layerName);
        handles.Add(frame);
        Box(10, 10, 410, 287);
        handles.Add(sheet.Segment(At(10, 42), At(410, 42), layerName));
        handles.Add(sheet.Fill(Rectangle(At(15, 26), At(300, 38)), null, layerName, BarColor));
        Label(18, 32, 7, job.Number, AttachmentPoint.MiddleLeft, BarTextColor);
        handles.Add(sheet.Segment(At(42, 27), At(42, 37), layerName, BarTextColor));
        Label(46, 32, 4, job.Site, AttachmentPoint.MiddleLeft, BarTextColor);
        Label(296, 32, 4.5, job.Title.ToUpperInvariant(), AttachmentPoint.MiddleRight, BarTextColor);
        Label(15, 20, 3, job.Date, AttachmentPoint.MiddleLeft);
        for (var index = 0; index < SignOffLabels.Length; index++)
        {
            var x = 60 + index * 60;
            Box(x, 17, x + 42, 25);
            Label(x + 21, 14, 2.5, SignOffLabels[index], AttachmentPoint.MiddleCenter);
        }
        Label(405, 12.5, 2.5, $"SHEET {number} / {count}    {sheetName.ToUpperInvariant()}    UNITS: {units.Label.ToUpperInvariant()}", AttachmentPoint.BottomRight);
        if (draft)
        {
            Label(15, 282, 6, "DRAFT — NOT FOR PRODUCTION", AttachmentPoint.TopLeft, DraftColor);
        }

        // Logo: the office block when the drawing or the block library has it.
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var logoPlaced = false;
        if (blockTable.Has(logo))
        {
            logoPlaced = FitBlock(transaction, sheet, blockTable[logo], At(310, 15), At(405, 38), layerName, handles);
        }
        if (!logoPlaced)
        {
            Label(357.5, 30, 7, "TRUST-LINES", AttachmentPoint.MiddleCenter);
            Label(357.5, 21, 3.5, "Design and Supply", AttachmentPoint.MiddleCenter);
        }

        // Material legend.
        if (hasLegend)
        {
            var top = 47 + 10 + legendRows * 9;
            Box(350, 47, 405, top);
            Label(353, top - 3, 3, "MATERIAL", AttachmentPoint.TopLeft);
            var y = top - 14.0;
            foreach (var row in legend)
            {
                Box(353, y, 363, y + 6);
                if (row.Swatch is not null)
                {
                    handles.Add(sheet.Fill(Rectangle(At(353, y), At(363, y + 6)), null, layerName, row.Swatch));
                }
                Label(366, y + 3, 2.2, $"RAL {row.Ral}\n{row.Name}", AttachmentPoint.MiddleLeft);
                y -= 9;
            }
            if (hanger)
            {
                using (var circle = new Circle(ToPoint(At(358, y + 4.5)), Vector3d.ZAxis, Size(1.5)))
                {
                    handles.Add(sheet.Add(circle, layerName));
                }
                handles.Add(sheet.Segment(At(358, y + 3), At(358, y + 0.5), layerName));
                handles.Add(sheet.Segment(At(356.5, y + 0.5), At(359.5, y + 0.5), layerName));
                Label(366, y + 3, 2.2, "HANGER", AttachmentPoint.MiddleLeft);
            }
        }

        // Save the sheet so plot_sheets_pdf can find it.
        var dictionary = SheetDictionary(database, transaction, OpenMode.ForWrite)!;
        var nextId = ReadNextSheetId(dictionary, transaction);
        var frameMin = At(0, 0);
        var frameMax = At(420, 297);
        var record = new SheetRecord(
            $"S{nextId}",
            sheetName,
            number,
            paper,
            frameMin.X,
            frameMin.Y,
            frameMax.X,
            frameMax.Y,
            frame,
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        );
        WriteXrecord(dictionary, transaction, record.Id, RoomRecords.Chunk(JsonSerializer.Serialize(record)));
        WriteXrecord(dictionary, transaction, SheetsMetaKey, new List<string> { (nextId + 1).ToString(CultureInfo.InvariantCulture) });
        transaction.Commit();

        var factor = 1 / units.Scale;
        document.Editor.WriteMessage($"\nClaude drew sheet {record.Id} ({sheetName}). Use UNDO to remove it.");
        return Success(
            $"Sheet {record.Id} drawn: {sheetName}, sheet {number} of {count}, {paper}"
                + (plotScale is double ratio ? $" at about 1:{ratio.ToString("0.#", CultureInfo.InvariantCulture)}." : "."),
            new
            {
                sheetId = record.Id,
                sheetName,
                sheetNumber = number,
                sheetCount = count,
                paper,
                scale = plotScale,
                draft,
                logo = logoPlaced ? logo : null,
                frame = new { min = Xy(ToPoint(frameMin), factor), max = Xy(ToPoint(frameMax), factor) },
                units = units.Label,
                layer = layerName,
                handles
            }
        );
    }

    private static List<LegendRow> ReadLegend(JsonElement arguments)
    {
        var rows = new List<LegendRow>();
        if (!HasValue(arguments, "materials"))
        {
            return rows;
        }
        var array = arguments.GetProperty("materials");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxLegendRows)
        {
            throw new ArgumentException($"materials must be an array of at most {MaxLegendRows} entries.");
        }
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException($"materials[{index}] must be an object.");
            }
            try
            {
                rows.Add(new LegendRow(
                    OptionalText(element, "name") ?? string.Empty,
                    OptionalText(element, "ral") ?? "TBD",
                    OptionalColorIndex(element, "aci")
                ));
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"materials[{index}].{exception.Message}");
            }
            index++;
        }
        return rows;
    }

    // Inserts a block scaled uniformly to fit the box, centred in it.
    private static bool FitBlock(
        Transaction transaction,
        SheetWriter sheet,
        ObjectId definitionId,
        Vec boxMin,
        Vec boxMax,
        string layerName,
        List<string> handles
    )
    {
        var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
        if (definition.IsLayout || definition.IsAnonymous || definition.IsFromExternalReference)
        {
            return false;
        }
        using var reference = new BlockReference(Point3d.Origin, definitionId);
        handles.Add(sheet.Add(reference, layerName));
        var extents = reference.GeometricExtents;
        var blockWidth = extents.MaxPoint.X - extents.MinPoint.X;
        var blockHeight = extents.MaxPoint.Y - extents.MinPoint.Y;
        if (blockWidth <= Tolerance || blockHeight <= Tolerance)
        {
            reference.Erase();
            handles.RemoveAt(handles.Count - 1);
            return false;
        }
        var scale = Math.Min((boxMax.X - boxMin.X) / blockWidth, (boxMax.Y - boxMin.Y) / blockHeight);
        var blockCentre = new Vec((extents.MinPoint.X + extents.MaxPoint.X) / 2, (extents.MinPoint.Y + extents.MaxPoint.Y) / 2);
        var boxCentre = (boxMin + boxMax) * 0.5;
        reference.ScaleFactors = new Scale3d(scale);
        reference.Position = ToPoint(boxCentre - blockCentre * scale);
        return true;
    }

    private static string ListSheets()
    {
        var document = RequireDocument();
        var database = document.Database;
        using var documentLock = document.LockDocument();
        using var transaction = database.TransactionManager.StartTransaction();
        var sheets = CheckedSheets(database, transaction);
        transaction.Commit();
        var missing = sheets.Count(sheet => !sheet.FrameExists);
        return Success(
            $"{sheets.Count} sheet(s) saved{(missing > 0 ? $", {missing} with their frame erased" : string.Empty)}.",
            new
            {
                drawingUnits = DrawingUnitsLabel(database),
                sheets = sheets.Select(sheet => new
                {
                    id = sheet.Record.Id,
                    name = sheet.Record.Name,
                    number = sheet.Record.Number,
                    paper = sheet.Record.Paper,
                    frame = new { min = new { x = sheet.Record.MinX, y = sheet.Record.MinY }, max = new { x = sheet.Record.MaxX, y = sheet.Record.MaxY } },
                    frameExists = sheet.FrameExists,
                    createdAt = sheet.Record.CreatedAt
                })
            }
        );
    }

    private static string CheckProductionBook(JsonElement arguments)
    {
        if (!HasValue(arguments, "spec"))
        {
            throw new ArgumentException("spec is required.");
        }
        var report = SpecCheck.Check(arguments.GetProperty("spec"));
        var errors = report.Findings.Count(finding => finding.Severity == SpecCheck.Error);
        var warnings = report.Findings.Count(finding => finding.Severity == SpecCheck.Warning);
        return Success(
            report.Issuable
                ? "The spec passes: it can be drawn and issued."
                : report.Drawable
                    ? $"The spec can be drawn as a DRAFT; {warnings} warning(s) must be resolved before issuing."
                    : $"The spec cannot be drawn: {errors} error(s).",
            new
            {
                drawable = report.Drawable,
                issuable = report.Issuable,
                errors,
                warnings,
                findings = report.Findings.Select(finding => new { severity = finding.Severity, part = finding.Part, message = finding.Message }),
                parts = report.Parts
            }
        );
    }

    private sealed record CheckedSheet(SheetRecord Record, bool FrameExists);

    // Saved sheets in book order (sheet number, then the order they were drawn).
    private static List<CheckedSheet> CheckedSheets(Database database, Transaction transaction)
    {
        var dictionary = SheetDictionary(database, transaction, OpenMode.ForRead);
        var sheets = new List<CheckedSheet>();
        if (dictionary is null)
        {
            return sheets;
        }
        foreach (DBDictionaryEntry entry in dictionary)
        {
            if (entry.Key == SheetsMetaKey)
            {
                continue;
            }
            SheetRecord record;
            try
            {
                record = JsonSerializer.Deserialize<SheetRecord>(string.Concat(XrecordText(transaction, entry.Value)))
                    ?? throw new FormatException("The record is empty.");
            }
            catch (System.Exception exception) when (exception is FormatException or JsonException)
            {
                throw new InvalidOperationException($"Saved sheet {entry.Key} in {SheetsKey} cannot be read ({exception.Message}).");
            }
            var exists = false;
            if (long.TryParse(record.FrameHandle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
                && database.TryGetObjectId(new Handle(value), out var id)
                && !id.IsErased)
            {
                exists = true;
            }
            sheets.Add(new CheckedSheet(record, exists));
        }
        return sheets
            .OrderBy(sheet => sheet.Record.Number)
            .ThenBy(sheet => int.TryParse(sheet.Record.Id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue)
            .ToList();
    }

    private static DBDictionary? SheetDictionary(Database database, Transaction transaction, OpenMode mode)
    {
        var named = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (named.Contains(SheetsKey))
        {
            return transaction.GetObject(named.GetAt(SheetsKey), mode) as DBDictionary
                ?? throw new InvalidOperationException($"{SheetsKey} in this drawing is not a bridge dictionary; nothing was changed.");
        }
        if (mode == OpenMode.ForRead)
        {
            return null;
        }
        named.UpgradeOpen();
        var dictionary = new DBDictionary();
        named.SetAt(SheetsKey, dictionary);
        transaction.AddNewlyCreatedDBObject(dictionary, true);
        return dictionary;
    }

    private static int ReadNextSheetId(DBDictionary dictionary, Transaction transaction)
    {
        var next = 1;
        if (dictionary.Contains(SheetsMetaKey)
            && int.TryParse(XrecordText(transaction, dictionary.GetAt(SheetsMetaKey)).FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var stored))
        {
            next = stored;
        }
        foreach (DBDictionaryEntry entry in dictionary)
        {
            if (entry.Key != SheetsMetaKey
                && int.TryParse(entry.Key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                next = Math.Max(next, number + 1);
            }
        }
        return next;
    }
}
