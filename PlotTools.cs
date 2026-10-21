using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;

namespace AutoCADClaudeBridge;

// Plots the sheets saved by draw_sheet into one multi-page PDF with AutoCAD's
// own "DWG To PDF.pc3" driver: each sheet's frame is a plot window, scaled to
// fit full-bleed A3 or A4 landscape.
internal static partial class BridgeHost
{
    private const string PdfDevice = "DWG To PDF.pc3";
    private const string PlotStyleTable = "acad.ctb";
    private static readonly Dictionary<string, string> PdfMedia = new()
    {
        ["A3"] = "ISO_full_bleed_A3_(420.00_x_297.00_MM)",
        ["A4"] = "ISO_full_bleed_A4_(297.00_x_210.00_MM)"
    };

    private static string PlotSheetsPdf(JsonElement arguments)
    {
        var document = RequireDocument();
        var database = document.Database;
        var overwrite = OptionalBoolean(arguments, "overwrite", false);
        var path = OptionalText(arguments, "path");
        if (path is null)
        {
            if (!document.IsNamedDrawing || !Path.IsPathRooted(database.Filename))
            {
                return Error("The drawing has not been saved yet, so there is no folder next to it. Pass path.");
            }
            path = Path.Combine(
                Path.GetDirectoryName(database.Filename)!,
                Path.GetFileNameWithoutExtension(database.Filename) + "-production-book.pdf"
            );
        }
        else if (!Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return Error("path must be a full Windows path ending in .pdf.");
        }
        path = Path.GetFullPath(path);
        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            return Error($"The folder {Path.GetDirectoryName(path)} does not exist.");
        }
        if (File.Exists(path) && !overwrite)
        {
            return Error($"{path} already exists. Ask the user, then pass overwrite: true to replace it.");
        }
        var requested = ReadSheetIds(arguments);
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
        {
            return Error("AutoCAD is already plotting. Wait for it to finish and try again.");
        }

        using var documentLock = document.LockDocument();
        using var transaction = database.TransactionManager.StartTransaction();
        var saved = CheckedSheets(database, transaction);
        var unknown = requested.Where(id => saved.All(sheet => sheet.Record.Id != id)).ToList();
        if (unknown.Count > 0)
        {
            return Error($"No saved sheet {string.Join(", ", unknown)}. Use list_sheets.");
        }
        var chosen = (requested.Count > 0 ? saved.Where(sheet => requested.Contains(sheet.Record.Id)) : saved).ToList();
        var skipped = chosen.Where(sheet => !sheet.FrameExists).Select(sheet => sheet.Record.Id).ToList();
        chosen = chosen.Where(sheet => sheet.FrameExists).ToList();
        if (chosen.Count == 0)
        {
            return Error(saved.Count == 0
                ? "No sheets are saved in this drawing. Use draw_sheet first."
                : "None of the chosen sheets still has its frame; draw them again with draw_sheet.");
        }

        var layout = (Layout)transaction.GetObject(LayoutManager.Current.GetLayoutId("Model"), OpenMode.ForRead);
        // The settings stay alive until the plot ends: each PlotInfo refers to its own.
        var settings = new List<PlotSettings>();
        var backgroundPlot = Application.GetSystemVariable("BACKGROUNDPLOT");
        try
        {
            var plots = chosen.Select(sheet => SheetPlotInfo(layout, sheet.Record, settings)).ToList();
            Application.SetSystemVariable("BACKGROUNDPLOT", 0);
            using var engine = PlotFactory.CreatePublishEngine();
            using var progress = new PlotProgressDialog(false, plots.Count, true);
            progress.OnBeginPlot();
            progress.IsVisible = false;
            engine.BeginPlot(progress, null);
            engine.BeginDocument(plots[0], document.Name, null, 1, true, path);
            for (var index = 0; index < plots.Count; index++)
            {
                progress.OnBeginSheet();
                var page = new PlotPageInfo();
                engine.BeginPage(page, plots[index], index == plots.Count - 1, null);
                engine.BeginGenerateGraphics(null);
                engine.EndGenerateGraphics(null);
                engine.EndPage(null);
                progress.OnEndSheet();
            }
            engine.EndDocument(null);
            progress.OnEndPlot();
            engine.EndPlot(null);
        }
        finally
        {
            Application.SetSystemVariable("BACKGROUNDPLOT", backgroundPlot);
            settings.ForEach(item => item.Dispose());
        }
        transaction.Commit();

        return Success(
            $"Plotted {chosen.Count} sheet(s) to {path}."
                + (skipped.Count > 0 ? $" Skipped {string.Join(", ", skipped)}: their frames were erased." : string.Empty),
            new
            {
                path,
                sheets = chosen.Select(sheet => new { id = sheet.Record.Id, name = sheet.Record.Name, number = sheet.Record.Number, paper = sheet.Record.Paper }),
                skipped
            }
        );
    }

    // One page: the sheet frame as the plot window, fitted and centred on the
    // full-bleed sheet so the frame lands on the paper edge.
    private static PlotInfo SheetPlotInfo(Layout layout, SheetRecord sheet, List<PlotSettings> keep)
    {
        var settings = new PlotSettings(layout.ModelType);
        keep.Add(settings);
        settings.CopyFrom(layout);
        var validator = PlotSettingsValidator.Current;
        validator.SetPlotConfigurationName(settings, PdfDevice, null);
        validator.RefreshLists(settings);
        var media = validator.GetCanonicalMediaNameList(settings).Cast<string>().ToList();
        var wanted = PdfMedia[sheet.Paper];
        var mediaName = media.Contains(wanted)
            ? wanted
            : media.FirstOrDefault(name => name.Contains($"_{sheet.Paper}_", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{PdfDevice} has no {sheet.Paper} paper size.");
        validator.SetCanonicalMediaName(settings, mediaName);
        validator.SetPlotWindowArea(settings, new Extents2d(sheet.MinX, sheet.MinY, sheet.MaxX, sheet.MaxY));
        validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
        validator.SetUseStandardScale(settings, true);
        validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
        validator.SetPlotCentered(settings, true);
        validator.SetPlotRotation(settings, PlotRotation.Degrees000);
        if (validator.GetPlotStyleSheetList().Cast<string>().Contains(PlotStyleTable, StringComparer.OrdinalIgnoreCase))
        {
            validator.SetCurrentStyleSheet(settings, PlotStyleTable);
        }

        var info = new PlotInfo { Layout = layout.ObjectId, OverrideSettings = settings };
        new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }.Validate(info);
        return info;
    }

    private static List<string> ReadSheetIds(JsonElement arguments)
    {
        var ids = new List<string>();
        if (!HasValue(arguments, "sheetIds"))
        {
            return ids;
        }
        var array = arguments.GetProperty("sheetIds");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 999)
        {
            throw new ArgumentException("sheetIds must be an array of sheet IDs such as S1.");
        }
        foreach (var element in array.EnumerateArray())
        {
            var id = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            if (id is null || id.Length < 2 || id[0] != 'S' || !id.AsSpan(1).ToString().All(char.IsAsciiDigit))
            {
                throw new ArgumentException("sheetIds must be sheet IDs such as S1.");
            }
            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }
        return ids;
    }
}
