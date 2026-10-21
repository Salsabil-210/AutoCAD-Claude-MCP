using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace AutoCADClaudeBridge;

// Checks a ceiling feature part spec (skills/ceiling-feature-production,
// references/part-spec.md) before anything is drawn or issued. Pure: it
// rebuilds every part with the same geometry the drawing tools use, so a spec
// that passes here draws without geometry errors. Kept free of AutoCAD types
// so it can be tested outside AutoCAD.
//
// Severity: "error" — the part cannot be drawn as specified; "warning" — it
// can be drawn but the book must not be issued until it is resolved; "info" —
// worth knowing.
internal sealed record SpecFinding(string Severity, string Part, string Message);

internal sealed record SpecReport(List<SpecFinding> Findings, List<Dictionary<string, object?>> Parts, bool Drawable, bool Issuable);

internal static class SpecCheck
{
    internal const string Error = "error";
    internal const string Warning = "warning";
    internal const string Info = "info";
    private static readonly string[] KnownUnits = { "mm", "cm", "dm", "m", "in", "ft" };
    // A rough capital letter width for estimating whether text fits a face.
    private const double LetterWidthRatio = 0.65;

    internal static SpecReport Check(JsonElement spec)
    {
        var findings = new List<SpecFinding>();
        var parts = new List<Dictionary<string, object?>>();
        void Add(string severity, string part, string message) => findings.Add(new SpecFinding(severity, part, message));

        if (spec.ValueKind != JsonValueKind.Object)
        {
            Add(Error, "", "The spec must be a JSON object.");
            return new SpecReport(findings, parts, false, false);
        }

        // Job -----------------------------------------------------------------
        var job = Child(spec, "job");
        var units = Text(job, "units");
        if (units is null || !KnownUnits.Contains(units))
        {
            Add(Error, "job", $"job.units must be one of {string.Join(", ", KnownUnits)}; every length in the spec is read in it.");
        }
        foreach (var field in new[] { "number", "site", "title", "date" })
        {
            if (string.IsNullOrWhiteSpace(Text(job, field)))
            {
                Add(Warning, "job", $"job.{field} is missing; the title block needs it.");
            }
        }

        // Materials -----------------------------------------------------------
        var materials = new HashSet<string>();
        foreach (var material in Items(spec, "materials"))
        {
            var id = Text(material, "id") ?? "?";
            if (!materials.Add(id))
            {
                Add(Error, "materials", $"Material id {id} is used twice.");
            }
            var ral = Text(material, "ral");
            if (string.IsNullOrWhiteSpace(ral) || ral.Equals("TBD", StringComparison.OrdinalIgnoreCase))
            {
                Add(Warning, "materials", $"Material {id} ({Text(material, "name") ?? "unnamed"}) has no RAL colour yet.");
            }
            if (Number(material, "aci") is null)
            {
                Add(Info, "materials", $"Material {id} has no aci, so its parts are drawn without fill.");
            }
        }

        // Parts ---------------------------------------------------------------
        var partIds = new HashSet<string>();
        var partList = Items(spec, "parts").ToList();
        if (partList.Count == 0)
        {
            Add(Error, "parts", "The spec has no parts.");
        }
        foreach (var part in partList)
        {
            var id = Text(part, "id") ?? "?";
            if (!partIds.Add(id))
            {
                Add(Error, id, $"Part id {id} is used twice.");
            }
            var name = Text(part, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                Add(Error, id, "The part has no name.");
            }
            var quantity = Number(part, "quantity");
            if (quantity is null || quantity < 1 || quantity != Math.Floor(quantity.Value))
            {
                Add(Error, id, "quantity must be a whole number of 1 or more.");
            }
            var material = Text(part, "material");
            if (material is null || !materials.Contains(material))
            {
                Add(Error, id, $"material {material ?? "(none)"} is not in materials.");
            }
            if (Child(part, "source") is { } source)
            {
                if (source.TryGetProperty("confirmed", out var confirmed) && confirmed.ValueKind == JsonValueKind.False)
                {
                    Add(Warning, id, "The designer has not confirmed this part's numbers (source.confirmed is false).");
                }
                // Values read off a render or taken from the standards, not given by the designer.
                var estimated = Items(source, "estimated")
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToList();
                if (estimated.Count > 0)
                {
                    Add(Warning, id, $"Estimated, not given by the designer: {string.Join(", ", estimated)}.");
                }
            }

            var summary = new Dictionary<string, object?> { ["id"] = id, ["type"] = Text(part, "type"), ["name"] = name };
            parts.Add(summary);
            try
            {
                switch (Text(part, "type"))
                {
                    case "band":
                        CheckBand(part, id, summary, Add);
                        break;
                    case "bent_bar":
                        CheckBentBar(part, id, summary, Add);
                        break;
                    default:
                        Add(Error, id, "type must be band or bent_bar.");
                        break;
                }
            }
            catch (ArgumentException exception)
            {
                Add(Error, id, exception.Message);
            }
        }

        // Assembly and open questions ----------------------------------------
        var assembly = Items(spec, "assembly").ToList();
        if (assembly.Count == 0)
        {
            Add(Warning, "assembly", "No assembly positions or hanger points: the workshop cannot see how the parts hang together.");
        }
        foreach (var entry in assembly)
        {
            var part = Text(entry, "part");
            if (part is null || !partIds.Contains(part))
            {
                Add(Error, "assembly", $"Assembly entry refers to part {part ?? "(none)"}, which is not in parts.");
            }
        }
        var hangers = Items(spec, "hangers").ToList();
        if (assembly.Count > 0 && hangers.Count == 0)
        {
            Add(Warning, "hangers", "No hanger points: the assembly plan cannot show how the feature is hung.");
        }
        var withoutDrop = hangers.Count(hanger => Number(hanger, "drop") is not > 0);
        if (withoutDrop > 0)
        {
            Add(Warning, "hangers", $"{withoutDrop} hanger point(s) have no drop length.");
        }
        var questions = Items(spec, "openQuestions").Count();
        if (questions > 0)
        {
            Add(Warning, "openQuestions", $"{questions} open question(s): the book can be drawn as a DRAFT only.");
        }

        var drawable = findings.All(finding => finding.Severity != Error);
        var issuable = drawable && findings.All(finding => finding.Severity != Warning);
        return new SpecReport(findings, parts, drawable, issuable);
    }

    private static void CheckBand(JsonElement part, string id, Dictionary<string, object?> summary, Action<string, string, string> add)
    {
        var points = Items(part, "points")
            .Select(point => new Vec(Number(point, "x") ?? double.NaN, Number(point, "y") ?? double.NaN))
            .ToList();
        if (points.Count < 3 || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            throw new ArgumentException("points must hold at least 3 corners with x and y.");
        }
        var radii = Items(part, "cornerRadii").Select(radius => radius.ValueKind == JsonValueKind.Number ? radius.GetDouble() : double.NaN).ToList();
        if (radii.Count == 0)
        {
            radii = Enumerable.Repeat(Number(part, "cornerRadius") ?? 0, points.Count).ToList();
        }
        var thickness = Required(part, "thickness");
        var height = Required(part, "height");
        var band = BandGeometry.Build(points, radii, thickness);
        summary["faces"] = band.Faces.ToDictionary(face => face.Label, face => (object?)Math.Round(face.EdgeLength, 2));
        summary["developedLength"] = Math.Round(band.Centre.Length, 2);

        if (Child(part, "lightStrip") is { } light)
        {
            var stripHeight = Required(light, "height");
            var offset = Number(light, "offset") ?? (height - stripHeight) / 2;
            if (stripHeight >= height || offset < 0 || offset + stripHeight > height + 1e-9)
            {
                add(Error, id, "lightStrip does not fit inside the band height.");
            }
        }

        var graphics = Items(part, "faceGraphics").ToList();
        var faces = band.Faces.ToDictionary(face => face.Label);
        var byFace = new Dictionary<string, List<string>>();
        foreach (var graphic in graphics)
        {
            var label = Text(graphic, "face") ?? "?";
            if (!faces.TryGetValue(label, out var face))
            {
                add(Error, id, $"faceGraphics refers to face {label}; this band has faces {string.Join(", ", faces.Keys)}.");
                continue;
            }
            byFace.TryAdd(label, new List<string>());
            byFace[label].Add(graphic.GetRawText());

            var (start, end) = GraphicSpan(graphic, face, height, id, add);
            if (double.IsNaN(start))
            {
                continue;
            }
            // Flat part of the face: graphics on the corner fillet bend with it.
            var startTangent = (face.FlatMidpoint - face.Start).Length - face.StraightLength / 2;
            var endTangent = face.EdgeLength - face.StraightLength - startTangent;
            if (start < startTangent - 1e-6 || end > face.EdgeLength - endTangent + 1e-6)
            {
                add(Warning, id, $"Face {label}: the {Text(graphic, "kind")} from {Fmt(start)} to {Fmt(end)} reach onto a corner fillet; the flat part is {Fmt(startTangent)} to {Fmt(face.EdgeLength - endTangent)}.");
            }
        }

        // Faces of equal length share one elevation unless groupFaces is false,
        // so they must carry the same graphics.
        var grouped = !(part.TryGetProperty("groupFaces", out var groupFaces) && groupFaces.ValueKind == JsonValueKind.False);
        if (grouped)
        {
            foreach (var elevation in BandGeometry.GroupFaces(band.Faces, true, 0.05).Where(elevation => elevation.Labels.Count > 1))
            {
                var signatures = elevation.Labels
                    .Select(label => byFace.TryGetValue(label, out var list) ? string.Join("|", list.Select(StripFace)) : string.Empty)
                    .Distinct()
                    .Count();
                if (signatures > 1)
                {
                    add(Error, id, $"Faces {string.Join(",", elevation.Labels)} are equal and share one elevation, but carry different graphics. Set groupFaces: false.");
                }
            }
        }
    }

    // Where a face graphic runs along its face, or NaN when it cannot be placed.
    private static (double Start, double End) GraphicSpan(
        JsonElement graphic,
        BandFace face,
        double bandHeight,
        string id,
        Action<string, string, string> add
    )
    {
        var kind = Text(graphic, "kind");
        if (kind == "chevrons")
        {
            var stroke = Required(graphic, "stroke");
            var chevronHeight = Number(graphic, "chevronHeight") ?? bandHeight;
            var row = FaceGraphicGeometry.Chevrons(
                face.EdgeLength,
                bandHeight,
                Number(graphic, "at") ?? throw new ArgumentException($"Chevrons on face {face.Label} need at."),
                (int)(Number(graphic, "count") ?? 2),
                stroke,
                Number(graphic, "pitch") ?? 2 * stroke,
                Number(graphic, "depth") ?? chevronHeight / 2,
                chevronHeight,
                Text(graphic, "pointing") != "left"
            );
            return (row.Start, row.End);
        }
        if (kind == "letters")
        {
            var text = Text(graphic, "text") ?? string.Empty;
            var letterHeight = Required(graphic, "letterHeight");
            if (text.Trim().Length == 0)
            {
                add(Error, id, $"Letters on face {face.Label} have no text.");
                return (double.NaN, double.NaN);
            }
            if (letterHeight > bandHeight)
            {
                add(Error, id, $"Letters on face {face.Label} are {Fmt(letterHeight)} high, taller than the {Fmt(bandHeight)} band.");
                return (double.NaN, double.NaN);
            }
            var width = text.Trim().Length * letterHeight * LetterWidthRatio;
            var start = Number(graphic, "at") ?? (face.EdgeLength - width) / 2;
            if (start < 0 || start + width > face.EdgeLength)
            {
                add(Warning, id, $"\"{text.Trim()}\" at {Fmt(letterHeight)} high is about {Fmt(width)} wide and may not fit face {face.Label} ({Fmt(face.EdgeLength)}); add_face_graphic measures it exactly.");
            }
            return (start, start + width);
        }
        add(Error, id, $"Face graphic kind {kind ?? "(none)"} must be letters or chevrons.");
        return (double.NaN, double.NaN);
    }

    private static void CheckBentBar(JsonElement part, string id, Dictionary<string, object?> summary, Action<string, string, string> add)
    {
        var start = Child(part, "start") ?? throw new ArgumentException("start is required.");
        var steps = new List<BarStep>();
        foreach (var segment in Items(part, "segments"))
        {
            if (Text(segment, "kind") == "arc")
            {
                var radius = Required(segment, "radius");
                var sweep = Number(segment, "angle") is double angle ? angle * Math.PI / 180 : Required(segment, "length") / radius;
                steps.Add(new BarStep(BentBarGeometry.Arc, 0, radius, Text(segment, "turn") == "right" ? -sweep : sweep));
            }
            else
            {
                steps.Add(new BarStep(BentBarGeometry.Line, Required(segment, "length"), 0, 0));
            }
        }
        var height = Required(part, "height");
        var bar = BentBarGeometry.Build(
            new Vec(
                Number(start, "x") ?? throw new ArgumentException("start.x is required."),
                Number(start, "y") ?? throw new ArgumentException("start.y is required.")
            ),
            (Number(part, "startAngle") ?? 0) * Math.PI / 180,
            steps,
            Required(part, "thickness")
        );
        var ends = Child(part, "ends");
        BentBarGeometry.Elevation(
            bar.Length,
            height,
            Text(ends, "start") ?? BentBarGeometry.Plain,
            Text(ends, "end") ?? BentBarGeometry.Plain,
            Number(part, "arrowHeight") ?? 0
        );
        summary["developedLength"] = Math.Round(bar.Length, 2);
        add(Info, id, $"Developed (cut) length {Fmt(bar.Length)}.");
    }

    // Two graphics are the same when they match apart from their face letter.
    private static string StripFace(string json)
    {
        using var document = JsonDocument.Parse(json);
        return string.Join(",", document.RootElement.EnumerateObject()
            .Where(property => property.Name != "face")
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{property.Name}={property.Value.GetRawText()}"));
    }

    private static JsonElement? Child(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var child)
        && child.ValueKind == JsonValueKind.Object
            ? child
            : null;

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string? Text(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var child)
        && child.ValueKind == JsonValueKind.String
            ? child.GetString()
            : null;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var child)
        && child.ValueKind == JsonValueKind.Number
        && double.IsFinite(child.GetDouble())
            ? child.GetDouble()
            : null;

    private static double Required(JsonElement element, string name) =>
        Number(element, name) is double value && value > 0
            ? value
            : throw new ArgumentException($"{name} must be a number greater than zero.");

    private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
