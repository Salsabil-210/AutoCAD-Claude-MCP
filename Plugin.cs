using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(AutoCADClaudeBridge.Commands))]
[assembly: ExtensionApplication(typeof(AutoCADClaudeBridge.Plugin))]

namespace AutoCADClaudeBridge;

public sealed class Plugin : IExtensionApplication
{
    public void Initialize()
    {
        BridgeHost.Start();
        Application.Idle += BridgeHost.OnIdle;
        Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
            "\nClaude AutoCAD Bridge loaded on localhost. Type CLAUDEBRIDGE for status."
        );
    }

    public void Terminate()
    {
        Application.Idle -= BridgeHost.OnIdle;
        BridgeHost.Stop();
    }
}

public static class Commands
{
    [CommandMethod("CLAUDEBRIDGE", CommandFlags.Modal)]
    public static void BridgeStatus()
    {
        var editor = Application.DocumentManager.MdiActiveDocument?.Editor;
        editor?.WriteMessage(
            BridgeHost.IsRunning
                ? "\nClaude bridge is running on 127.0.0.1:51327."
                : "\nClaude bridge is not running. Restart AutoCAD and reload the plug-in."
        );
    }
}

internal static class BridgeHost
{
    private const int Port = 51327;
    private const int MaxPoints = 10_000;
    private const int MaxTextLength = 2_000;
    private const int MaxHandles = 1_000;
    private const int MaxListLimit = 1_000;
    private const double MaxSize = 1_000_000;
    private const double MaxCoordinate = 1_000_000_000;
    private const double Tolerance = 1e-9;
    private static readonly string[] TextAttachments =
    {
        "TopLeft", "TopCenter", "TopRight",
        "MiddleLeft", "MiddleCenter", "MiddleRight",
        "BottomLeft", "BottomCenter", "BottomRight"
    };
    // Units Claude may tag a call with. Values arrive exactly as the user
    // stated them and are converted to the drawing's INSUNITS here, so the
    // model never has to convert lengths itself.
    private static readonly Dictionary<string, UnitsValue> InputUnits =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["mm"] = UnitsValue.Millimeters,
            ["cm"] = UnitsValue.Centimeters,
            ["dm"] = UnitsValue.Decimeters,
            ["m"] = UnitsValue.Meters,
            ["km"] = UnitsValue.Kilometers,
            ["in"] = UnitsValue.Inches,
            ["ft"] = UnitsValue.Feet,
            ["yd"] = UnitsValue.Yards,
            ["mi"] = UnitsValue.Miles
        };
    private static readonly string AccessToken = TokenStore.LoadOrCreate();
    private static readonly ConcurrentQueue<PendingRequest> Queue = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static CancellationTokenSource? _cancellation;
    private static TcpListener? _listener;

    internal static bool IsRunning => _listener is not null;

    internal static void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _ = AcceptLoopAsync(_cancellation.Token);
    }

    internal static void Stop()
    {
        _cancellation?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cancellation?.Dispose();
        _cancellation = null;
        
    }

    private static async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var listener = _listener;
                if (listener is null)
                {
                    return;
                }

                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private static async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken
    )
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true)
        {
            AutoFlush = true
        })
        {
            try
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                var request = JsonSerializer.Deserialize<BridgeRequest>(line, JsonOptions);
                if (request is null || request.Token != AccessToken)
                {
                    await writer.WriteLineAsync(Error("Unauthorized local request."));
                    return;
                }

                var completion = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                Queue.Enqueue(new PendingRequest(request, completion));

                var response = await completion.Task.WaitAsync(
                    TimeSpan.FromSeconds(30),
                    cancellationToken
                );
                await writer.WriteLineAsync(response);
            }
            catch (TimeoutException)
            {
                await writer.WriteLineAsync(Error("AutoCAD did not process the request in time."));
            }
            catch (System.Exception exception)
            {
                await writer.WriteLineAsync(Error(exception.Message));
            }
        }
    }

    internal static void OnIdle(object? sender, EventArgs eventArgs)
    {
        var processed = 0;
        while (processed < 8 && Queue.TryDequeue(out var pending))
        {
            try
            {
                pending.Completion.TrySetResult(Process(pending.Request));
            }
            catch (System.Exception exception)
            {
                pending.Completion.TrySetResult(Error(exception.Message));
            }

            processed++;
        }
    }

    private static string Process(BridgeRequest request)
    {
        return request.Tool switch
        {
            "autocad_status" => Status(),
            "draw_line" => DrawLine(request.Arguments),
            "draw_rectangle" => DrawRectangle(request.Arguments),
            "draw_polyline" => DrawPolyline(request.Arguments),
            "add_linear_dimension" => AddLinearDimension(request.Arguments),
            "add_aligned_dimension" => AddAlignedDimension(request.Arguments),
            "add_angular_dimension" => AddAngularDimension(request.Arguments),
            "add_hatch" => AddHatch(request.Arguments),
            "add_text" => AddText(request.Arguments),
            "add_leader" => AddLeader(request.Arguments),
            "list_entities" => ListEntities(request.Arguments),
            "erase_entities" => EraseEntities(request.Arguments),
            "delete_layer" => DeleteLayer(request.Arguments),
            "move_entities" => MoveEntities(request.Arguments),
            "set_dimension_props" => SetDimensionProps(request.Arguments),
            "zoom_extents" => ZoomExtents(),
            _ => Error("Unknown or disabled AutoCAD tool.")
        };
    }

    private static string Status()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document is null)
        {
            return Error("AutoCAD is running, but no drawing is open.");
        }

        var database = document.Database;
        return Success(
            "AutoCAD is connected.",
            new
            {
                drawing = document.Name,
                port = Port,
                units = DrawingUnitsLabel(database),
                insunits = database.Insunits.ToString(),
                lunits = database.Lunits,
                measurement = database.Measurement.ToString(),
                dimlfac = database.Dimlfac,
                dimscale = database.Dimscale
            }
        );
    }

    private static string DrawLine(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var start = Point(arguments, "startX", "startY", units.Scale);
        var end = Point(arguments, "endX", "endY", units.Scale);
        var layerName = LayerName(arguments);

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        EnsureLayer(document.Database, transaction, layerName);
        var modelSpace = OpenModelSpace(document.Database, transaction);

        using var line = new Line(start, end)
        {
            Layer = layerName
        };
        modelSpace.AppendEntity(line);
        transaction.AddNewlyCreatedDBObject(line, true);
        var handle = line.Handle.ToString();
        var length = line.Length / units.Scale;
        transaction.Commit();

        document.Editor.WriteMessage("\nClaude created one line. Use UNDO to remove it.");
        return Success(
            "Line created.",
            new { handle, layer = layerName, length, units = units.Label }
        );
    }

    private static string DrawRectangle(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var corner = Point(arguments, "x", "y", units.Scale);
        var width = PositiveLength(arguments, "width", units.Scale);
        var height = PositiveLength(arguments, "height", units.Scale);
        var x = corner.X;
        var y = corner.Y;
        var layerName = LayerName(arguments);

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        EnsureLayer(document.Database, transaction, layerName);
        var modelSpace = OpenModelSpace(document.Database, transaction);

        using var rectangle = new Polyline(4)
        {
            Closed = true,
            Layer = layerName
        };
        rectangle.AddVertexAt(0, new Point2d(x, y), 0, 0, 0);
        rectangle.AddVertexAt(1, new Point2d(x + width, y), 0, 0, 0);
        rectangle.AddVertexAt(2, new Point2d(x + width, y + height), 0, 0, 0);
        rectangle.AddVertexAt(3, new Point2d(x, y + height), 0, 0, 0);
        modelSpace.AppendEntity(rectangle);
        transaction.AddNewlyCreatedDBObject(rectangle, true);
        var handle = rectangle.Handle.ToString();
        transaction.Commit();

        document.Editor.WriteMessage(
            "\nClaude created one rectangle. Use UNDO to remove it."
        );
        return Success(
            "Rectangle created.",
            new
            {
                handle,
                layer = layerName,
                x = x / units.Scale,
                y = y / units.Scale,
                width = width / units.Scale,
                height = height / units.Scale,
                units = units.Label
            }
        );
    }

    private static string DrawPolyline(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var closed = OptionalBoolean(arguments, "closed", false);
        var points = ReadPoints(arguments, "points", closed ? 3 : 2, units.Scale);
        var layerName = LayerName(arguments);
        var color = ColorValue(arguments);

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        EnsureLayer(document.Database, transaction, layerName);
        var modelSpace = OpenModelSpace(document.Database, transaction);

        using var polyline = new Polyline(points.Count);
        polyline.SetDatabaseDefaults();
        polyline.Layer = layerName;
        polyline.Color = color;
        for (var index = 0; index < points.Count; index++)
        {
            polyline.AddVertexAt(index, points[index], 0, 0, 0);
        }
        polyline.Closed = closed;
        modelSpace.AppendEntity(polyline);
        transaction.AddNewlyCreatedDBObject(polyline, true);
        var handle = polyline.Handle.ToString();
        var length = polyline.Length / units.Scale;
        transaction.Commit();

        document.Editor.WriteMessage("\nClaude created one polyline. Use UNDO to remove it.");
        return Success(
            "Polyline created.",
            new
            {
                handle,
                layer = layerName,
                vertices = points.Count,
                closed,
                length,
                units = units.Label
            }
        );
    }

    private static string AddLinearDimension(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var first = Point(arguments, "x1", "y1", units.Scale);
        var second = Point(arguments, "x2", "y2", units.Scale);
        var dimensionLine = Point(arguments, "dimLineX", "dimLineY", units.Scale);
        var rotation = DegreesToRadians(OptionalAngle(arguments, "rotation", 0));
        var textOverride = OptionalText(arguments, "textOverride");
        var textHeight = OptionalPositive(arguments, "textHeight", units.Scale);
        var layerName = LayerName(arguments);
        var color = ColorValue(arguments);
        RequireDistinct(first, second, "The two measured points must be different.");

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        EnsureLayer(document.Database, transaction, layerName);
        var modelSpace = OpenModelSpace(document.Database, transaction);

        using var dimension = new RotatedDimension(
            rotation,
            first,
            second,
            dimensionLine,
            textOverride ?? string.Empty,
            document.Database.Dimstyle
        );
        var handle = AppendDimension(
            modelSpace,
            transaction,
            dimension,
            layerName,
            color,
            textHeight
        );
        var measurement = dimension.Measurement / units.Scale;
        transaction.Commit();

        document.Editor.WriteMessage(
            "\nClaude created one linear dimension. Use UNDO to remove it."
        );
        return Success(
            "Linear dimension created.",
            new { handle, layer = layerName, measurement, units = units.Label }
        );
    }

    private static string AddAlignedDimension(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var first = Point(arguments, "x1", "y1", units.Scale);
        var second = Point(arguments, "x2", "y2", units.Scale);
        var offset = Length(arguments, "offset", units.Scale);
        var textOverride = OptionalText(arguments, "textOverride");
        var textHeight = OptionalPositive(arguments, "textHeight", units.Scale);
        var layerName = LayerName(arguments);
        var color = ColorValue(arguments);
        RequireDistinct(first, second, "The two measured points must be different.");
        if (Math.Abs(offset) < Tolerance || Math.Abs(offset) > MaxSize)
        {
            throw new ArgumentException("offset must be non-zero and within the drawing size limit.");
        }

        var direction = (second - first).GetNormal();
        var normal = new Vector3d(-direction.Y, direction.X, 0);
        var midpoint = new Point3d(
            (first.X + second.X) / 2,
            (first.Y + second.Y) / 2,
            0
        );
        var dimensionLine = midpoint + normal * offset;

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        EnsureLayer(document.Database, transaction, layerName);
        var modelSpace = OpenModelSpace(document.Database, transaction);

        using var dimension = new AlignedDimension(
            first,
            second,
            dimensionLine,
            textOverride ?? string.Empty,
            document.Database.Dimstyle
        );
        var handle = AppendDimension(
            modelSpace,
            transaction,
            dimension,
            layerName,
            color,
            textHeight
        );
        var measurement = dimension.Measurement / units.Scale;
        transaction.Commit();

        document.Editor.WriteMessage(
            "\nClaude created one aligned dimension. Use UNDO to remove it."
        );
        return Success(
            "Aligned dimension created.",
            new { handle, layer = layerName, measurement, units = units.Label }
        );
    }

    private static string AddAngularDimension(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var (line1Start, line1End) = LineArgument(arguments, "line1", units.Scale);
        var (line2Start, line2End) = LineArgument(arguments, "line2", units.Scale);
        var arcPoint = Point(arguments, "arcX", "arcY", units.Scale);
        var textHeight = OptionalPositive(arguments, "textHeight", units.Scale);
        var layerName = LayerName(arguments);
        var color = ColorValue(arguments);
        var cross = (line1End - line1Start).GetNormal()
            .CrossProduct((line2End - line2Start).GetNormal());
        if (cross.Length < 1e-6)
        {
            throw new ArgumentException("line1 and line2 must not be parallel.");
        }

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        EnsureLayer(document.Database, transaction, layerName);
        var modelSpace = OpenModelSpace(document.Database, transaction);

        using var dimension = new LineAngularDimension2(
            line1Start,
            line1End,
            line2Start,
            line2End,
            arcPoint,
            string.Empty,
            document.Database.Dimstyle
        );
        var handle = AppendDimension(
            modelSpace,
            transaction,
            dimension,
            layerName,
            color,
            textHeight
        );
        var degrees = RadiansToDegrees(dimension.Measurement);
        transaction.Commit();

        document.Editor.WriteMessage(
            "\nClaude created one angular dimension. Use UNDO to remove it."
        );
        return Success(
            "Angular dimension created.",
            new { handle, layer = layerName, measurement = degrees }
        );
    }

    private static string AddHatch(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var boundary = ReadPolygon(arguments, "boundary", units.Scale);
        var pattern = PatternName(arguments);
        // patternScale is a ratio, not a length, so it is never unit-converted.
        var patternScale = OptionalPositive(arguments, "patternScale", 1)
            ?? DefaultPatternScale(document.Database);
        var patternAngle = DegreesToRadians(OptionalAngle(arguments, "patternAngle", 0));
        var layerName = LayerName(arguments);
        var color = ColorValue(arguments);

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        EnsureLayer(document.Database, transaction, layerName);
        var modelSpace = OpenModelSpace(document.Database, transaction);

        var loopPoints = new Point2dCollection();
        foreach (var point in boundary)
        {
            loopPoints.Add(point);
        }
        loopPoints.Add(boundary[0]);
        var bulges = new DoubleCollection(new double[loopPoints.Count]);

        using var hatch = new Hatch();
        hatch.SetDatabaseDefaults();
        hatch.Layer = layerName;
        hatch.Color = color;
        modelSpace.AppendEntity(hatch);
        transaction.AddNewlyCreatedDBObject(hatch, true);
        hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
        if (!string.Equals(pattern, "SOLID", StringComparison.OrdinalIgnoreCase))
        {
            hatch.PatternScale = patternScale;
            hatch.PatternAngle = patternAngle;
        }
        hatch.Associative = false;
        hatch.AppendLoop(HatchLoopTypes.Outermost, loopPoints, bulges);
        hatch.EvaluateHatch(true);
        var handle = hatch.Handle.ToString();
        transaction.Commit();

        document.Editor.WriteMessage("\nClaude created one hatch. Use UNDO to remove it.");
        return Success(
            "Hatch created.",
            new
            {
                handle,
                layer = layerName,
                pattern,
                patternScale,
                vertices = boundary.Count,
                units = units.Label
            }
        );
    }

    private static string AddText(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var location = Point(arguments, "x", "y", units.Scale);
        var text = RequiredText(arguments, "text");
        var height = PositiveSize(arguments, "height", units.Scale);
        var rotation = DegreesToRadians(OptionalAngle(arguments, "rotation", 0));
        var attachment = Attachment(arguments);
        var layerName = LayerName(arguments);
        var color = ColorValue(arguments);

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        EnsureLayer(document.Database, transaction, layerName);
        var modelSpace = OpenModelSpace(document.Database, transaction);

        using var mtext = new MText();
        mtext.SetDatabaseDefaults();
        mtext.Layer = layerName;
        mtext.Color = color;
        mtext.Location = location;
        mtext.TextHeight = height;
        mtext.Rotation = rotation;
        mtext.Attachment = attachment;
        mtext.Contents = MTextContents(text);
        modelSpace.AppendEntity(mtext);
        transaction.AddNewlyCreatedDBObject(mtext, true);
        var handle = mtext.Handle.ToString();
        transaction.Commit();

        document.Editor.WriteMessage("\nClaude created one text object. Use UNDO to remove it.");
        return Success("Text created.", new { handle, layer = layerName, units = units.Label });
    }

    private static string AddLeader(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var arrow = Point(arguments, "arrowX", "arrowY", units.Scale);
        var landing = Point(arguments, "landingX", "landingY", units.Scale);
        var text = RequiredText(arguments, "text");
        var textHeight = PositiveSize(arguments, "textHeight", units.Scale);
        var layerName = LayerName(arguments);
        var color = ColorValue(arguments);
        RequireDistinct(arrow, landing, "The arrow and landing points must be different.");

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        EnsureLayer(document.Database, transaction, layerName);
        var modelSpace = OpenModelSpace(document.Database, transaction);

        using var content = new MText();
        content.SetDatabaseDefaults();
        content.Contents = MTextContents(text);
        content.TextHeight = textHeight;
        content.Location = landing;

        using var leader = new MLeader();
        leader.SetDatabaseDefaults();
        leader.Layer = layerName;
        leader.Color = color;
        leader.ContentType = ContentType.MTextContent;
        leader.MText = content;
        leader.TextHeight = textHeight;
        var leaderIndex = leader.AddLeader();
        var lineIndex = leader.AddLeaderLine(leaderIndex);
        leader.AddFirstVertex(lineIndex, arrow);
        leader.AddLastVertex(lineIndex, landing);
        modelSpace.AppendEntity(leader);
        transaction.AddNewlyCreatedDBObject(leader, true);
        var handle = leader.Handle.ToString();
        transaction.Commit();

        document.Editor.WriteMessage("\nClaude created one leader. Use UNDO to remove it.");
        return Success("Leader created.", new { handle, layer = layerName, units = units.Label });
    }

    private static string ListEntities(JsonElement arguments)
    {
        var layerFilter = OptionalName(arguments, "layer", "layer");
        var typeFilter = OptionalName(arguments, "type", "type");
        var limit = OptionalInteger(arguments, "limit", 200, 1, MaxListLimit);
        var document = RequireDocument();
        var units = Units(arguments, document.Database);

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)transaction.GetObject(
            document.Database.BlockTableId,
            OpenMode.ForRead
        );
        var modelSpace = (BlockTableRecord)transaction.GetObject(
            blockTable[BlockTableRecord.ModelSpace],
            OpenMode.ForRead
        );

        var entities = new List<object>();
        var matched = 0;
        foreach (ObjectId id in modelSpace)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            var type = id.ObjectClass.DxfName;
            if (layerFilter is not null
                && !string.Equals(entity.Layer, layerFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (typeFilter is not null
                && !string.Equals(type, typeFilter, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.GetType().Name, typeFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matched++;
            if (entities.Count < limit)
            {
                entities.Add(new
                {
                    handle = entity.Handle.ToString(),
                    type,
                    kind = entity.GetType().Name,
                    layer = entity.Layer,
                    colorIndex = entity.ColorIndex,
                    geometry = Describe(entity, 1 / units.Scale)
                });
            }
        }

        transaction.Commit();
        return Success(
            $"Found {matched} matching entities; returned {entities.Count}.",
            new
            {
                total = matched,
                truncated = matched > entities.Count,
                units = units.Label,
                entities
            }
        );
    }

    private static string EraseEntities(JsonElement arguments)
    {
        var handles = ReadHandles(arguments);
        var document = RequireDocument();

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        var (found, notFound) = ResolveEntities(document.Database, transaction, handles);
        if (found.Count == 0)
        {
            return Error($"None of the handles were found in model space: {string.Join(", ", notFound)}.");
        }

        var erased = new List<string>();
        foreach (var entity in found)
        {
            entity.UpgradeOpen();
            entity.Erase();
            erased.Add(entity.Handle.ToString());
        }
        transaction.Commit();

        document.Editor.WriteMessage(
            $"\nClaude erased {erased.Count} entities. Use UNDO to restore them."
        );
        return Success($"Erased {erased.Count} entities.", new { erased, notFound });
    }

    private static string DeleteLayer(JsonElement arguments)
    {
        var layerName = OptionalName(arguments, "layer", "layer")
            ?? throw new ArgumentException("layer is required.");
        if (layerName == "0"
            || string.Equals(layerName, "Defpoints", StringComparison.OrdinalIgnoreCase))
        {
            return Error($"Layer {layerName} is protected and cannot be deleted.");
        }

        var document = RequireDocument();
        var database = document.Database;

        using var documentLock = document.LockDocument();
        using var transaction = database.TransactionManager.StartTransaction();
        var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (!layerTable.Has(layerName))
        {
            return Error($"Layer {layerName} does not exist.");
        }

        var layerId = layerTable[layerName];
        if (layerId == database.Clayer)
        {
            return Error($"Layer {layerName} is the current layer. Set another current layer first.");
        }

        var layer = (LayerTableRecord)transaction.GetObject(layerId, OpenMode.ForRead);
        if (layer.IsLocked)
        {
            return Error($"Layer {layerName} is locked. Unlock it first.");
        }

        var erased = 0;
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId blockId in blockTable)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (!block.IsLayout)
            {
                continue;
            }

            foreach (ObjectId id in block)
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity
                    && entity.LayerId == layerId)
                {
                    entity.UpgradeOpen();
                    entity.Erase();
                    erased++;
                }
            }
        }

        var purgeable = new ObjectIdCollection { layerId };
        database.Purge(purgeable);
        if (purgeable.Count == 0)
        {
            return Error(
                $"Layer {layerName} is still referenced by block definitions or other objects; nothing was changed."
            );
        }

        layer.UpgradeOpen();
        layer.Erase();
        transaction.Commit();

        document.Editor.WriteMessage(
            $"\nClaude erased {erased} entities and deleted layer {layerName}. Use UNDO to restore them."
        );
        return Success(
            $"Erased {erased} entities and deleted layer {layerName}.",
            new { layer = layerName, erasedEntities = erased }
        );
    }

    private static string MoveEntities(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var handles = ReadHandles(arguments);
        var dx = Length(arguments, "dx", units.Scale);
        var dy = Length(arguments, "dy", units.Scale);

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        var (found, notFound) = ResolveEntities(document.Database, transaction, handles);
        if (found.Count == 0)
        {
            return Error($"None of the handles were found in model space: {string.Join(", ", notFound)}.");
        }

        var displacement = Matrix3d.Displacement(new Vector3d(dx, dy, 0));
        var moved = new List<string>();
        foreach (var entity in found)
        {
            entity.UpgradeOpen();
            entity.TransformBy(displacement);
            moved.Add(entity.Handle.ToString());
        }
        transaction.Commit();

        document.Editor.WriteMessage(
            $"\nClaude moved {moved.Count} entities. Use UNDO to move them back."
        );
        return Success(
            $"Moved {moved.Count} entities.",
            new
            {
                moved,
                notFound,
                dx = dx / units.Scale,
                dy = dy / units.Scale,
                units = units.Label
            }
        );
    }

    private static string SetDimensionProps(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var handle = ReadHandle(arguments, "handle");
        var textHeight = OptionalPositive(arguments, "textHeight", units.Scale);
        var textGap = OptionalNonNegative(arguments, "textGap", units.Scale);
        var arrowSize = OptionalNonNegative(arguments, "arrowSize", units.Scale);
        var textOverride = OptionalText(arguments, "textOverride");
        var hasColor = HasValue(arguments, "color");
        var color = ColorValue(arguments);
        if (textHeight is null && textGap is null && arrowSize is null
            && textOverride is null && !hasColor)
        {
            throw new ArgumentException("Provide at least one property to change.");
        }

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        var (found, _) = ResolveEntities(document.Database, transaction, new List<string> { handle });
        if (found.Count == 0 || found[0] is not Dimension dimension)
        {
            return Error($"Handle {handle} is not a dimension in model space.");
        }

        dimension.UpgradeOpen();
        if (textHeight is double height)
        {
            dimension.Dimtxt = height;
        }
        if (textGap is double gap)
        {
            dimension.Dimgap = gap;
        }
        if (arrowSize is double arrow)
        {
            dimension.Dimasz = arrow;
        }
        if (textOverride is not null)
        {
            dimension.DimensionText = textOverride;
        }
        if (hasColor)
        {
            dimension.Color = color;
        }
        dimension.RecomputeDimensionBlock(true);
        transaction.Commit();

        document.Editor.WriteMessage("\nClaude updated one dimension. Use UNDO to revert it.");
        return Success(
            "Dimension updated.",
            new { handle = dimension.Handle.ToString(), layer = dimension.Layer }
        );
    }

    private static string ZoomExtents()
    {
        var document = RequireDocument();
        var database = document.Database;
        if (!database.TileMode)
        {
            return Error("Switch to the Model tab before zooming to extents.");
        }

        using var documentLock = document.LockDocument();
        database.UpdateExt(true);
        var minimum = database.Extmin;
        var maximum = database.Extmax;
        if (minimum.X > maximum.X || minimum.Y > maximum.Y)
        {
            return Error("The drawing is empty; there are no extents to zoom to.");
        }

        using var view = document.Editor.GetCurrentView();
        var worldToEye = (
            Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)
            * Matrix3d.Displacement(view.Target - Point3d.Origin)
            * Matrix3d.PlaneToWorld(view.ViewDirection)
        ).Inverse();
        var extents = new Extents3d(minimum, maximum);
        extents.TransformBy(worldToEye);
        var width = Math.Max(extents.MaxPoint.X - extents.MinPoint.X, Tolerance);
        var height = Math.Max(extents.MaxPoint.Y - extents.MinPoint.Y, Tolerance);
        var aspect = view.Width / view.Height;
        if (width / height < aspect)
        {
            width = height * aspect;
        }
        else
        {
            height = width / aspect;
        }

        view.CenterPoint = new Point2d(
            (extents.MinPoint.X + extents.MaxPoint.X) / 2,
            (extents.MinPoint.Y + extents.MaxPoint.Y) / 2
        );
        view.Width = width * 1.05;
        view.Height = height * 1.05;
        document.Editor.SetCurrentView(view);

        return Success(
            "Zoomed to drawing extents.",
            new
            {
                min = new { x = minimum.X, y = minimum.Y },
                max = new { x = maximum.X, y = maximum.Y },
                units = DrawingUnitsLabel(database)
            }
        );
    }

    private static string AppendDimension(
        BlockTableRecord modelSpace,
        Transaction transaction,
        Dimension dimension,
        string layerName,
        Color color,
        double? textHeight
    )
    {
        dimension.SetDatabaseDefaults();
        dimension.Layer = layerName;
        dimension.Color = color;
        modelSpace.AppendEntity(dimension);
        transaction.AddNewlyCreatedDBObject(dimension, true);
        if (textHeight is double height)
        {
            dimension.Dimtxt = height;
        }
        dimension.RecomputeDimensionBlock(true);
        return dimension.Handle.ToString();
    }

    // factor converts drawing units to the units the caller asked for.
    private static object? Describe(Entity entity, double factor)
    {
        return entity switch
        {
            Line line => new
            {
                start = Xy(line.StartPoint, factor),
                end = Xy(line.EndPoint, factor),
                length = line.Length * factor
            },
            Polyline polyline => new
            {
                closed = polyline.Closed,
                length = polyline.Length * factor,
                vertices = Enumerable.Range(0, Math.Min(polyline.NumberOfVertices, MaxPoints))
                    .Select(index => Xy(polyline.GetPoint2dAt(index), factor))
                    .ToList()
            },
            Dimension dimension => new
            {
                measurement = dimension is LineAngularDimension2
                    ? RadiansToDegrees(dimension.Measurement)
                    : dimension.Measurement * factor,
                textOverride = dimension.DimensionText,
                textHeight = dimension.Dimtxt * factor,
                arrowSize = dimension.Dimasz * factor,
                textGap = dimension.Dimgap * factor,
                textPosition = Xy(dimension.TextPosition, factor)
            },
            MText mtext => new
            {
                text = mtext.Text,
                location = Xy(mtext.Location, factor),
                height = mtext.TextHeight * factor
            },
            DBText text => new
            {
                text = text.TextString,
                location = Xy(text.Position, factor),
                height = text.Height * factor
            },
            MLeader leader => DescribeLeader(leader, factor),
            Hatch hatch => new
            {
                pattern = hatch.PatternName,
                loops = hatch.NumberOfLoops,
                associative = hatch.Associative
            },
            Circle circle => new
            {
                center = Xy(circle.Center, factor),
                radius = circle.Radius * factor
            },
            Arc arc => new
            {
                center = Xy(arc.Center, factor),
                radius = arc.Radius * factor,
                startAngle = RadiansToDegrees(arc.StartAngle),
                endAngle = RadiansToDegrees(arc.EndAngle)
            },
            _ => null
        };
    }

    private static object DescribeLeader(MLeader leader, double factor)
    {
        if (leader.ContentType != ContentType.MTextContent || leader.MText is not MText content)
        {
            return new { text = (string?)null };
        }

        using (content)
        {
            var leaderIndexes = leader.GetLeaderIndexes();
            var lineIndexes = leaderIndexes.Count > 0 && leaderIndexes[0] is int leaderIndex
                ? leader.GetLeaderLineIndexes(leaderIndex)
                : new System.Collections.ArrayList();
            var arrow = lineIndexes.Count > 0 && lineIndexes[0] is int lineIndex
                ? Xy(leader.GetFirstVertex(lineIndex), factor)
                : null;
            return new { text = content.Text, textLocation = Xy(content.Location, factor), arrow };
        }
    }

    private static (List<Entity> Found, List<string> NotFound) ResolveEntities(
        Database database,
        Transaction transaction,
        List<string> handles
    )
    {
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var modelSpaceId = blockTable[BlockTableRecord.ModelSpace];
        var found = new List<Entity>();
        var notFound = new List<string>();

        foreach (var value in handles)
        {
            var handle = new Handle(Convert.ToInt64(value, 16));
            if (database.TryGetObjectId(handle, out var id)
                && !id.IsErased
                && transaction.GetObject(id, OpenMode.ForRead) is Entity entity
                && entity.OwnerId == modelSpaceId)
            {
                found.Add(entity);
            }
            else
            {
                notFound.Add(value);
            }
        }

        return (found, notFound);
    }

    private static Document RequireDocument()
    {
        return Application.DocumentManager.MdiActiveDocument
            ?? throw new InvalidOperationException("No active AutoCAD drawing is open.");
    }

    private static BlockTableRecord OpenModelSpace(Database database, Transaction transaction)
    {
        var blockTable = (BlockTable)transaction.GetObject(
            database.BlockTableId,
            OpenMode.ForRead
        );
        return (BlockTableRecord)transaction.GetObject(
            blockTable[BlockTableRecord.ModelSpace],
            OpenMode.ForWrite
        );
    }

    private static void EnsureLayer(
        Database database,
        Transaction transaction,
        string layerName
    )
    {
        var layerTable = (LayerTable)transaction.GetObject(
            database.LayerTableId,
            OpenMode.ForRead
        );
        if (layerTable.Has(layerName))
        {
            return;
        }

        layerTable.UpgradeOpen();
        var layer = new LayerTableRecord { Name = layerName };
        layerTable.Add(layer);
        transaction.AddNewlyCreatedDBObject(layer, true);
    }

    private static string LayerName(JsonElement arguments)
    {
        var name = arguments.TryGetProperty("layer", out var property)
            ? property.GetString() ?? "AI_DRAWING"
            : "AI_DRAWING";
        if (name.Length is < 1 or > 64)
        {
            throw new ArgumentException("Layer must contain 1 to 64 characters.");
        }

        foreach (var character in name)
        {
            if (!char.IsLetterOrDigit(character) && character is not '_' and not '-')
            {
                throw new ArgumentException(
                    "Layer may contain only letters, numbers, underscores, and hyphens."
                );
            }
        }

        return name;
    }

    private static double RequiredNumber(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var property)
            || !property.TryGetDouble(out var value)
            || !double.IsFinite(value)
            || Math.Abs(value) > MaxCoordinate)
        {
            throw new ArgumentException($"{name} must be a finite drawing coordinate.");
        }

        return value;
    }

    private static double PositiveNumber(JsonElement arguments, string name)
    {
        var value = RequiredNumber(arguments, name);
        if (value <= 0)
        {
            throw new ArgumentException($"{name} must be greater than zero.");
        }

        return value;
    }

    // Unit handling: scale converts the caller's units to drawing units.
    private readonly record struct UnitContext(double Scale, string Label);

    private static UnitContext Units(JsonElement arguments, Database database)
    {
        var units = OptionalText(arguments, "units");
        if (units is null)
        {
            return new UnitContext(1, DrawingUnitsLabel(database));
        }

        if (!InputUnits.TryGetValue(units, out var from))
        {
            throw new ArgumentException(
                $"units must be one of: {string.Join(", ", InputUnits.Keys)}."
            );
        }

        if (database.Insunits == UnitsValue.Undefined)
        {
            throw new ArgumentException(
                "The drawing is unitless (INSUNITS = 0), so " + units + " values cannot be converted. "
                + "Ask the user what one drawing unit represents, or set INSUNITS in AutoCAD and try again."
            );
        }

        return new UnitContext(
            UnitsConverter.GetConversionFactor(from, database.Insunits),
            units.ToLowerInvariant()
        );
    }

    private static string DrawingUnitsLabel(Database database)
    {
        foreach (var (code, value) in InputUnits)
        {
            if (value == database.Insunits)
            {
                return code;
            }
        }

        return database.Insunits == UnitsValue.Undefined
            ? "unitless"
            : database.Insunits.ToString();
    }

    // Predefined patterns are defined in millimetres (acadiso.pat) for metric
    // drawings and in inches (acad.pat) otherwise; scale them to INSUNITS so a
    // hatch looks the same whether the drawing is in mm, cm or m.
    private static double DefaultPatternScale(Database database)
    {
        if (database.Insunits == UnitsValue.Undefined)
        {
            return 1;
        }

        var patternUnits = database.Measurement == MeasurementValue.Metric
            ? UnitsValue.Millimeters
            : UnitsValue.Inches;
        return UnitsConverter.GetConversionFactor(patternUnits, database.Insunits);
    }

    private static double ToDrawing(double value, double scale, string name)
    {
        var converted = value * scale;
        if (!double.IsFinite(converted) || Math.Abs(converted) > MaxCoordinate)
        {
            throw new ArgumentException($"{name} is outside the drawing coordinate limit after unit conversion.");
        }

        return converted;
    }

    private static double Length(JsonElement arguments, string name, double scale)
    {
        return ToDrawing(RequiredNumber(arguments, name), scale, name);
    }

    private static double PositiveLength(JsonElement arguments, string name, double scale)
    {
        return ToDrawing(PositiveNumber(arguments, name), scale, name);
    }

    private static bool HasValue(JsonElement arguments, string name)
    {
        return arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(name, out var property)
            && property.ValueKind != JsonValueKind.Null;
    }

    private static double? OptionalPositive(JsonElement arguments, string name, double scale)
    {
        return HasValue(arguments, name) ? PositiveSize(arguments, name, scale) : null;
    }

    private static double PositiveSize(JsonElement arguments, string name, double scale)
    {
        var value = PositiveLength(arguments, name, scale);
        if (value > MaxSize)
        {
            throw new ArgumentException($"{name} must not exceed {MaxSize} drawing units.");
        }

        return value;
    }

    private static double? OptionalNonNegative(JsonElement arguments, string name, double scale)
    {
        if (!HasValue(arguments, name))
        {
            return null;
        }

        var value = Length(arguments, name, scale);
        if (value < 0 || value > MaxSize)
        {
            throw new ArgumentException($"{name} must be between 0 and {MaxSize} drawing units.");
        }

        return value;
    }

    private static double OptionalAngle(JsonElement arguments, string name, double fallback)
    {
        if (!HasValue(arguments, name))
        {
            return fallback;
        }

        var value = RequiredNumber(arguments, name);
        if (Math.Abs(value) > 360)
        {
            throw new ArgumentException($"{name} must be between -360 and 360 degrees.");
        }

        return value;
    }

    private static int OptionalInteger(
        JsonElement arguments,
        string name,
        int fallback,
        int minimum,
        int maximum
    )
    {
        if (!HasValue(arguments, name))
        {
            return fallback;
        }

        if (!arguments.GetProperty(name).TryGetInt32(out var value)
            || value < minimum
            || value > maximum)
        {
            throw new ArgumentException($"{name} must be an integer from {minimum} to {maximum}.");
        }

        return value;
    }

    private static bool OptionalBoolean(JsonElement arguments, string name, bool fallback)
    {
        if (!HasValue(arguments, name))
        {
            return fallback;
        }

        var property = arguments.GetProperty(name);
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException($"{name} must be true or false.")
        };
    }

    private static string RequiredText(JsonElement arguments, string name)
    {
        var text = OptionalText(arguments, name);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException($"{name} must not be empty.");
        }

        return text;
    }

    private static string? OptionalText(JsonElement arguments, string name)
    {
        if (!HasValue(arguments, name))
        {
            return null;
        }

        var property = arguments.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"{name} must be a string.");
        }

        var text = property.GetString() ?? string.Empty;
        if (text.Length > MaxTextLength)
        {
            throw new ArgumentException($"{name} must not exceed {MaxTextLength} characters.");
        }

        return text;
    }

    private static string? OptionalName(JsonElement arguments, string name, string label)
    {
        var value = OptionalText(arguments, name);
        if (value is null)
        {
            return null;
        }

        if (value.Length is < 1 or > 64
            || !value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-'))
        {
            throw new ArgumentException(
                $"{label} must contain 1 to 64 letters, numbers, underscores, or hyphens."
            );
        }

        return value;
    }

    private static string PatternName(JsonElement arguments)
    {
        return (OptionalName(arguments, "pattern", "pattern") ?? "SOLID").ToUpperInvariant();
    }

    private static AttachmentPoint Attachment(JsonElement arguments)
    {
        var value = OptionalText(arguments, "attachment") ?? "TopLeft";
        var match = TextAttachments.FirstOrDefault(
            name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase)
        ) ?? throw new ArgumentException(
            $"attachment must be one of: {string.Join(", ", TextAttachments)}."
        );
        return Enum.Parse<AttachmentPoint>(match);
    }

    private static Color ColorValue(JsonElement arguments)
    {
        if (!HasValue(arguments, "color"))
        {
            return Color.FromColorIndex(ColorMethod.ByLayer, 256);
        }

        if (!arguments.GetProperty("color").TryGetInt32(out var index) || index is < 1 or > 255)
        {
            throw new ArgumentException("color must be an AutoCAD Color Index from 1 to 255.");
        }

        return Color.FromColorIndex(ColorMethod.ByAci, (short)index);
    }

    private static Point3d Point(JsonElement arguments, string xName, string yName, double scale)
    {
        return new Point3d(
            Length(arguments, xName, scale),
            Length(arguments, yName, scale),
            0
        );
    }

    private static Point3d PointElement(JsonElement element, string label, double scale)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"{label} must be an object with x and y.");
        }

        try
        {
            return Point(element, "x", "y", scale);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"{label}: {exception.Message}");
        }
    }

    private static (Point3d Start, Point3d End) LineArgument(
        JsonElement arguments,
        string name,
        double scale
    )
    {
        if (!HasValue(arguments, name) || arguments.GetProperty(name).ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"{name} must be an object with start and end points.");
        }

        var line = arguments.GetProperty(name);
        if (!line.TryGetProperty("start", out var start) || !line.TryGetProperty("end", out var end))
        {
            throw new ArgumentException($"{name} must contain start and end points.");
        }

        var startPoint = PointElement(start, $"{name}.start", scale);
        var endPoint = PointElement(end, $"{name}.end", scale);
        RequireDistinct(startPoint, endPoint, $"{name} start and end must be different.");
        return (startPoint, endPoint);
    }

    private static List<Point2d> ReadPoints(
        JsonElement arguments,
        string name,
        int minimum,
        double scale
    )
    {
        if (!HasValue(arguments, name) || arguments.GetProperty(name).ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"{name} must be an array of points.");
        }

        var array = arguments.GetProperty(name);
        var count = array.GetArrayLength();
        if (count < minimum || count > MaxPoints)
        {
            throw new ArgumentException($"{name} must contain {minimum} to {MaxPoints} points.");
        }

        var points = new List<Point2d>(count);
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var point = PointElement(element, $"{name}[{index}]", scale);
            points.Add(new Point2d(point.X, point.Y));
            index++;
        }

        return points;
    }

    private static List<Point2d> ReadPolygon(JsonElement arguments, string name, double scale)
    {
        var points = ReadPoints(arguments, name, 3, scale);
        var cleaned = new List<Point2d>(points.Count);
        foreach (var point in points)
        {
            if (cleaned.Count == 0 || cleaned[^1].GetDistanceTo(point) > Tolerance)
            {
                cleaned.Add(point);
            }
        }
        if (cleaned.Count > 1 && cleaned[0].GetDistanceTo(cleaned[^1]) <= Tolerance)
        {
            cleaned.RemoveAt(cleaned.Count - 1);
        }
        if (cleaned.Count < 3)
        {
            throw new ArgumentException($"{name} must contain at least 3 distinct points.");
        }

        var doubleArea = 0.0;
        for (var index = 0; index < cleaned.Count; index++)
        {
            var current = cleaned[index];
            var next = cleaned[(index + 1) % cleaned.Count];
            doubleArea += current.X * next.Y - next.X * current.Y;
        }
        if (Math.Abs(doubleArea) / 2 <= Tolerance)
        {
            throw new ArgumentException($"{name} must enclose a non-zero area.");
        }

        return cleaned;
    }

    private static string ReadHandle(JsonElement arguments, string name)
    {
        var value = OptionalText(arguments, name) ?? throw new ArgumentException($"{name} is required.");
        return NormalizeHandle(value, name);
    }

    private static List<string> ReadHandles(JsonElement arguments)
    {
        if (!HasValue(arguments, "handles")
            || arguments.GetProperty("handles").ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("handles must be an array of entity handles.");
        }

        var array = arguments.GetProperty("handles");
        var count = array.GetArrayLength();
        if (count < 1 || count > MaxHandles)
        {
            throw new ArgumentException($"handles must contain 1 to {MaxHandles} entries.");
        }

        var handles = new List<string>(count);
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("Every handle must be a string.");
            }

            var handle = NormalizeHandle(element.GetString() ?? string.Empty, "handle");
            if (!handles.Contains(handle))
            {
                handles.Add(handle);
            }
        }

        return handles;
    }

    private static string NormalizeHandle(string value, string name)
    {
        if (value.Length is < 1 or > 16 || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException($"{name} must be a hexadecimal AutoCAD handle.");
        }

        return value.ToUpperInvariant();
    }

    private static void RequireDistinct(Point3d first, Point3d second, string message)
    {
        if (first.DistanceTo(second) <= Tolerance)
        {
            throw new ArgumentException(message);
        }
    }

    private static string MTextContents(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("{", "\\{")
            .Replace("}", "\\}")
            .Replace("\r\n", "\n")
            .Replace("\n", "\\P");
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    private static double RadiansToDegrees(double radians) => radians * 180 / Math.PI;

    private static object Xy(Point3d point, double factor) =>
        new { x = point.X * factor, y = point.Y * factor };

    private static object Xy(Point2d point, double factor) =>
        new { x = point.X * factor, y = point.Y * factor };

    private static string Success(string message, object data)
    {
        return JsonSerializer.Serialize(new { ok = true, message, data });
    }

    private static string Error(string message)
    {
        return JsonSerializer.Serialize(new { ok = false, message, error = message });
    }

    private sealed record BridgeRequest(string Token, string Tool, JsonElement Arguments);
    private sealed record PendingRequest(
        BridgeRequest Request,
        TaskCompletionSource<string> Completion
    );
}

internal static class TokenStore
{
    private const int TokenBytes = 32;

    private static string TokenFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoCADClaudeBridge",
        "bridge-token.txt"
    );

    internal static string LoadOrCreate()
    {
        var tokenFile = TokenFilePath;
        var directory = Path.GetDirectoryName(tokenFile)
            ?? throw new InvalidOperationException("Could not resolve the local token folder.");
        Directory.CreateDirectory(directory);

        if (File.Exists(tokenFile))
        {
            return ReadAndValidate(tokenFile);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes))
            .ToLowerInvariant();

        try
        {
            using var stream = new FileStream(
                tokenFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read
            );
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(token);
        }
        catch (IOException) when (File.Exists(tokenFile))
        {
            return ReadAndValidate(tokenFile);
        }

        return token;
    }

    private static string ReadAndValidate(string tokenFile)
    {
        var token = File.ReadAllText(tokenFile, Encoding.UTF8).Trim();
        if (token.Length != TokenBytes * 2 || !token.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                $"The bridge token is invalid. Delete {tokenFile} and restart AutoCAD."
            );
        }

        return token;
    }
}
