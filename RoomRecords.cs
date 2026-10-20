using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoCADClaudeBridge;

internal sealed record SourceWall(string Handle, string Fingerprint);

// One saved room. Geometry is stored in drawing units (INSUNITS at save time);
// MetresPerUnit converts it to metres and is null in unitless drawings.
internal sealed class RoomRecord
{
    public int Schema { get; set; } = RoomRecords.Schema;
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string Status { get; set; } = "ok";
    public List<string> Warnings { get; set; } = new();
    public List<double[]> Boundary { get; set; } = new();
    public List<List<double[]>> Holes { get; set; } = new();
    public double NetArea { get; set; }
    public double GrossArea { get; set; }
    public double Perimeter { get; set; }
    public string Units { get; set; } = string.Empty;
    public double? MetresPerUnit { get; set; }
    public string MeasurementConvention { get; set; } = RoomRecords.MeasurementConvention;
    public List<SourceWall> SourceWalls { get; set; } = new();
    public List<string> LabelHandles { get; set; } = new();
    public string DetectionVersion { get; set; } = RoomRecords.DetectionVersion;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

internal sealed record ScheduleRow(
    string Id,
    string? Name,
    string Status,
    double NetSquareMetres,
    double GrossSquareMetres,
    double PerimeterMetres,
    IReadOnlyList<string> Warnings
);

// Pure logic behind saved rooms: wall fingerprints, staleness, matching a new
// detection to saved rooms, storage format and the schedule CSV. Kept free of
// AutoCAD types so it can be tested outside AutoCAD.
internal static class RoomRecords
{
    internal const int Schema = 1;
    internal const string DetectionVersion = "detect_rooms/1";
    internal const string MeasurementConvention =
        "Gross area is the area inside the room outline; net area is gross area minus holes "
        + "(columns, shafts). Perimeter is the length of the outline, holes excluded.";
    internal static readonly string[] SavedStatuses = { "ok", "needs_review", "confirmed" };

    // Smallest share of the larger of two outlines they must have in common
    // to count as the same room when the labels do not decide it.
    internal const double MinOverlap = 0.5;
    private const int OverlapSamples = 64;
    private const int ChunkLength = 250;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // A short hash of one wall entity's layer and geometry, rounded so that
    // re-reading an unchanged entity always gives the same value.
    internal static string Fingerprint(string layer, IEnumerable<WallSegment> segments)
    {
        var text = new StringBuilder(layer.ToUpperInvariant());
        foreach (var segment in segments)
        {
            text.Append(';')
                .Append(Round(segment.Start.X)).Append(',').Append(Round(segment.Start.Y)).Append(',')
                .Append(Round(segment.End.X)).Append(',').Append(Round(segment.End.Y));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    private static string Round(double value) =>
        (Math.Round(value, 6) + 0.0).ToString("R", CultureInfo.InvariantCulture);

    // Handles of the walls an outline (and its holes) runs along: every
    // boundary edge is a piece of a wall unless it closes a gap.
    internal static List<string> SourceWallHandles(
        RoomFace room,
        IReadOnlyList<WallSegment> walls,
        double tolerance
    )
    {
        var handles = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var ring in room.Holes.Prepend(room.Boundary))
        {
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                foreach (var wall in walls)
                {
                    if (DistanceToSegment(a, wall) <= 2 * tolerance
                        && DistanceToSegment(b, wall) <= 2 * tolerance)
                    {
                        handles.Add(wall.Source);
                    }
                }
            }
        }

        return handles.ToList();
    }

    private static double DistanceToSegment(Vec point, WallSegment wall)
    {
        var direction = wall.End - wall.Start;
        var lengthSquared = Vec.Dot(direction, direction);
        var t = lengthSquared == 0
            ? 0
            : Math.Clamp(Vec.Dot(point - wall.Start, direction) / lengthSquared, 0, 1);
        return (point - (wall.Start + direction * t)).Length;
    }

    // Why a saved room no longer matches the drawing; empty when it still
    // does. current maps each saved handle to its fingerprint now, or null
    // when the entity is gone or no longer a wall.
    internal static List<string> StaleReasons(
        IReadOnlyList<SourceWall> saved,
        IReadOnlyDictionary<string, string?> current
    )
    {
        var reasons = new List<string>();
        foreach (var wall in saved)
        {
            if (!current.TryGetValue(wall.Handle, out var fingerprint) || fingerprint is null)
            {
                reasons.Add($"Wall {wall.Handle} was erased or is no longer a wall.");
            }
            else if (fingerprint != wall.Fingerprint)
            {
                reasons.Add($"Wall {wall.Handle} was moved, reshaped or put on another layer.");
            }
        }

        return reasons;
    }

    // Gives each detected room the ID of the saved room it is: first by a
    // name that is unique on both sides, then by the largest outline overlap.
    // Returns null for rooms that are new. A saved ID is used at most once.
    internal static string?[] Match(
        IReadOnlyList<(string Id, string? Name, IReadOnlyList<Vec> Boundary)> saved,
        IReadOnlyList<(string? Name, IReadOnlyList<Vec> Boundary)> detected,
        double minOverlap = MinOverlap
    )
    {
        var ids = new string?[detected.Count];
        var used = new HashSet<int>();

        var savedNames = UniqueNames(saved.Select(room => room.Name));
        var detectedNames = UniqueNames(detected.Select(room => room.Name));
        for (var i = 0; i < detected.Count; i++)
        {
            var name = Key(detected[i].Name);
            if (name is null
                || !detectedNames.Contains(name)
                || !savedNames.Contains(name))
            {
                continue;
            }

            var match = Enumerable.Range(0, saved.Count).First(index => Key(saved[index].Name) == name);
            ids[i] = saved[match].Id;
            used.Add(match);
        }

        var pairs = new List<(double Overlap, int Detected, int Saved)>();
        for (var i = 0; i < detected.Count; i++)
        {
            if (ids[i] is not null)
            {
                continue;
            }
            for (var j = 0; j < saved.Count; j++)
            {
                if (used.Contains(j))
                {
                    continue;
                }
                var overlap = Overlap(detected[i].Boundary, saved[j].Boundary);
                if (overlap >= minOverlap)
                {
                    pairs.Add((overlap, i, j));
                }
            }
        }

        foreach (var (_, i, j) in pairs.OrderByDescending(pair => pair.Overlap))
        {
            if (ids[i] is null && used.Add(j))
            {
                ids[i] = saved[j].Id;
            }
        }

        return ids;
    }

    private static string? Key(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim().ToUpperInvariant();

    private static HashSet<string> UniqueNames(IEnumerable<string?> names)
    {
        return names
            .Select(Key)
            .OfType<string>()
            .GroupBy(name => name)
            .Where(group => group.Count() == 1)
            .Select(group => group.Key)
            .ToHashSet();
    }

    // Shared area of two outlines over the larger of their areas, estimated
    // on a grid over the box both outlines cover.
    internal static double Overlap(IReadOnlyList<Vec> first, IReadOnlyList<Vec> second)
    {
        var minX = Math.Max(first.Min(p => p.X), second.Min(p => p.X));
        var maxX = Math.Min(first.Max(p => p.X), second.Max(p => p.X));
        var minY = Math.Max(first.Min(p => p.Y), second.Min(p => p.Y));
        var maxY = Math.Min(first.Max(p => p.Y), second.Max(p => p.Y));
        if (maxX <= minX || maxY <= minY)
        {
            return 0;
        }

        var stepX = (maxX - minX) / OverlapSamples;
        var stepY = (maxY - minY) / OverlapSamples;
        var inBoth = 0;
        for (var i = 0; i < OverlapSamples; i++)
        {
            for (var j = 0; j < OverlapSamples; j++)
            {
                var point = new Vec(minX + (i + 0.5) * stepX, minY + (j + 0.5) * stepY);
                if (FloorPlanGeometry.InPolygon(first, point) && FloorPlanGeometry.InPolygon(second, point))
                {
                    inBoth++;
                }
            }
        }

        var shared = inBoth * stepX * stepY;
        var larger = Math.Max(
            Math.Abs(FloorPlanGeometry.SignedArea(first)),
            Math.Abs(FloorPlanGeometry.SignedArea(second))
        );
        return larger <= 0 ? 0 : shared / larger;
    }

    // A confirmation survives a re-save only when nothing about the room
    // changed: same name, same walls with the same geometry, same area.
    internal static bool KeepsConfirmation(RoomRecord saved, RoomRecord detected)
    {
        return saved.Status == "confirmed"
            && detected.Status != "needs_review"
            && Key(saved.Name) == Key(detected.Name)
            && saved.SourceWalls.SequenceEqual(detected.SourceWalls)
            && Math.Abs(saved.NetArea - detected.NetArea)
                <= 1e-9 * Math.Max(1, Math.Abs(saved.NetArea));
    }

    internal static List<double[]> ToPoints(IEnumerable<Vec> ring) =>
        ring.Select(point => new[] { point.X, point.Y }).ToList();

    internal static List<Vec> ToVecs(IEnumerable<double[]> ring) =>
        ring.Select(point => new Vec(point[0], point[1])).ToList();

    // Xrecord strings are kept short, so a record is stored as JSON split
    // into chunks.
    internal static List<string> Serialize(RoomRecord record) =>
        Chunk(JsonSerializer.Serialize(record, JsonOptions));

    internal static RoomRecord Deserialize(IEnumerable<string> chunks)
    {
        var record = JsonSerializer.Deserialize<RoomRecord>(string.Concat(chunks), JsonOptions)
            ?? throw new FormatException("Empty room record.");
        if (record.Schema != Schema
            || string.IsNullOrEmpty(record.Id)
            || !SavedStatuses.Contains(record.Status)
            || record.Boundary.Count < 3
            || record.Boundary.Any(point => point is not { Length: 2 }))
        {
            throw new FormatException($"Room record {record.Id} is not a valid bridge record.");
        }

        return record;
    }

    internal static List<string> Chunk(string text)
    {
        var chunks = new List<string>();
        for (var start = 0; start < text.Length; start += ChunkLength)
        {
            chunks.Add(text.Substring(start, Math.Min(ChunkLength, text.Length - start)));
        }

        return chunks;
    }

    internal static string ScheduleCsv(IEnumerable<ScheduleRow> rows)
    {
        var csv = new StringBuilder();
        csv.Append("Room ID,Name,Status,Net area (m²),Gross area (m²),Perimeter (m),Warnings\r\n");
        foreach (var row in rows)
        {
            csv.Append(Cell(row.Id)).Append(',')
                .Append(Cell(row.Name ?? string.Empty)).Append(',')
                .Append(Cell(row.Status)).Append(',')
                .Append(Number(row.NetSquareMetres)).Append(',')
                .Append(Number(row.GrossSquareMetres)).Append(',')
                .Append(Number(row.PerimeterMetres)).Append(',')
                .Append(Cell(string.Join("; ", row.Warnings)))
                .Append("\r\n");
        }

        return csv.ToString();
    }

    private static string Number(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    // Quotes cells that need it, and keeps text a spreadsheet would read as a
    // formula as plain text.
    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
