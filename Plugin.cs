using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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

internal static partial class BridgeHost
{
    private const int Port = 51327;
    private const int MaxPoints = 10_000;
    private const int MaxTextLength = 2_000;
    private const int MaxHandles = 1_000;
    private const int MaxListLimit = 1_000;
    private const double MaxSize = 1_000_000;
    private const double MaxCoordinate = 1_000_000_000;
    private const double Tolerance = 1e-9;
    // Named Objects Dictionary entry holding the floor plan type, so it is
    // saved with the DWG and follows UNDO.
    private const string FloorPlanKey = "CLAUDE_BRIDGE_FLOOR_PLAN";
    // Named Objects Dictionary entry holding saved rooms: one Xrecord per
    // room ID plus META with the next room number.
    private const string RoomsKey = "CLAUDE_BRIDGE_ROOMS";
    private const string RoomsMetaKey = "META";
    private static readonly string[] PlanTypes =
    {
        "architectural", "furniture", "electrical", "plumbing", "reflected_ceiling", "other"
    };
    // Room detection compares every wall piece with every other one, so the
    // wall layers are capped to keep a call well inside the 30 s timeout.
    private const int MaxWallSegments = 5_000;
    private const int MaxLayerNames = 20;
    private const int MaxRoomVertices = 500;
    private const int MaxRoomBlocks = 200;
    // Room-name attributes on room tag blocks.
    private static readonly string[] RoomNameTags = { "NAME", "ROOM", "ROOM_NAME", "ROOMNAME", "ROOM_NO", "ROOMNO" };
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
            "draw_circle" => DrawCircle(request.Arguments),
            "draw_arc" => DrawArc(request.Arguments),
            "draw_walls" => DrawWalls(request.Arguments),
            "place_furniture" => PlaceFurniture(request.Arguments),
            "insert_block" => InsertBlock(request.Arguments),
            "copy_entities" => CopyEntities(request.Arguments),
            "rotate_entities" => RotateEntities(request.Arguments),
            "mirror_entities" => MirrorEntities(request.Arguments),
            "floor_plan_summary" => FloorPlanSummary(request.Arguments),
            "query_region" => QueryRegion(request.Arguments),
            "set_floor_plan_type" => SetFloorPlanType(request.Arguments),
            "detect_rooms" => DetectRooms(request.Arguments),
            "save_rooms" => SaveRooms(request.Arguments),
            "get_rooms" => GetRooms(request.Arguments),
            "export_room_schedule" => ExportRoomSchedule(request.Arguments),
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
                entities.Add(EntityEntry(id, entity, 1 / units.Scale));
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

    private static string FloorPlanSummary(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var factor = 1 / units.Scale;

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        var modelSpace = ReadModelSpace(document.Database, transaction);

        var layers = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var types = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var blocks = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Extents3d? extents = null;
        var total = 0;
        foreach (ObjectId id in modelSpace)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            total++;
            Count(layers, entity.Layer);
            Count(types, id.ObjectClass.DxfName);
            if (entity is BlockReference reference)
            {
                Count(blocks, BlockName(reference));
            }
            if (entity.Bounds is Extents3d box)
            {
                if (extents is Extents3d current)
                {
                    current.AddExtents(box);
                    extents = current;
                }
                else
                {
                    extents = box;
                }
            }
        }

        var planType = ReadPlanType(document.Database, transaction);
        transaction.Commit();
        return Success(
            $"Floor plan has {total} model-space entities.",
            new
            {
                planType,
                total,
                units = units.Label,
                extents = extents is Extents3d planExtents
                    ? new
                    {
                        min = Xy(planExtents.MinPoint, factor),
                        max = Xy(planExtents.MaxPoint, factor),
                        width = (planExtents.MaxPoint.X - planExtents.MinPoint.X) * factor,
                        height = (planExtents.MaxPoint.Y - planExtents.MinPoint.Y) * factor
                    }
                    : null,
                layers,
                types,
                blocks
            }
        );
    }

    private static string QueryRegion(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var minimum = Point(arguments, "minX", "minY", units.Scale);
        var maximum = Point(arguments, "maxX", "maxY", units.Scale);
        if (maximum.X <= minimum.X || maximum.Y <= minimum.Y)
        {
            throw new ArgumentException("maxX and maxY must be greater than minX and minY.");
        }

        var inside = (OptionalText(arguments, "mode") ?? "crossing").ToLowerInvariant() switch
        {
            "crossing" => false,
            "inside" => true,
            _ => throw new ArgumentException("mode must be crossing or inside.")
        };
        var layerFilter = OptionalName(arguments, "layer", "layer");
        var limit = OptionalInteger(arguments, "limit", 200, 1, MaxListLimit);

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        var modelSpace = ReadModelSpace(document.Database, transaction);

        var entities = new List<object>();
        var matched = 0;
        foreach (ObjectId id in modelSpace)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity
                || entity.Bounds is not Extents3d box)
            {
                continue;
            }
            if (layerFilter is not null
                && !string.Equals(entity.Layer, layerFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hit = inside
                ? box.MinPoint.X >= minimum.X && box.MinPoint.Y >= minimum.Y
                    && box.MaxPoint.X <= maximum.X && box.MaxPoint.Y <= maximum.Y
                : box.MinPoint.X <= maximum.X && box.MaxPoint.X >= minimum.X
                    && box.MinPoint.Y <= maximum.Y && box.MaxPoint.Y >= minimum.Y;
            if (!hit)
            {
                continue;
            }

            matched++;
            if (entities.Count < limit)
            {
                entities.Add(EntityEntry(id, entity, 1 / units.Scale));
            }
        }

        transaction.Commit();
        return Success(
            $"Found {matched} entities in the region; returned {entities.Count}.",
            new
            {
                total = matched,
                truncated = matched > entities.Count,
                mode = inside ? "inside" : "crossing",
                units = units.Label,
                entities
            }
        );
    }

    private static string SetFloorPlanType(JsonElement arguments)
    {
        var value = OptionalText(arguments, "planType") ?? throw new ArgumentException("planType is required.");
        var planType = PlanTypes.FirstOrDefault(
            name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase)
        ) ?? throw new ArgumentException($"planType must be one of: {string.Join(", ", PlanTypes)}.");

        var document = RequireDocument();
        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        var dictionary = (DBDictionary)transaction.GetObject(
            document.Database.NamedObjectsDictionaryId,
            OpenMode.ForWrite
        );
        using var data = new ResultBuffer(new TypedValue((int)DxfCode.Text, planType));
        if (dictionary.Contains(FloorPlanKey))
        {
            if (transaction.GetObject(dictionary.GetAt(FloorPlanKey), OpenMode.ForWrite) is not Xrecord existing)
            {
                return Error($"{FloorPlanKey} in this drawing is not a bridge record; nothing was changed.");
            }
            existing.Data = data;
        }
        else
        {
            using var record = new Xrecord { Data = data };
            dictionary.SetAt(FloorPlanKey, record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }
        transaction.Commit();

        document.Editor.WriteMessage($"\nClaude set the floor plan type to {planType}. Use UNDO to revert it.");
        return Success("Floor plan type saved in the drawing.", new { planType });
    }

    // Finds rooms as the closed areas formed by walls on the given layers,
    // closes doorway gaps along the wall line, flags open wall ends, and
    // assigns labels and blocks to the room that contains them. Read-only.
    private static string DetectRooms(JsonElement arguments)
    {
        var document = RequireDocument();
        var database = document.Database;
        var units = Units(arguments, database);
        var factor = 1 / units.Scale;

        using var documentLock = document.LockDocument();
        using var transaction = database.TransactionManager.StartTransaction();
        var scan = ScanRooms(arguments, database, transaction, units);
        var detection = scan.Detection;

        var assignedLabels = new HashSet<string>();
        var rooms = new List<object>();
        var roomsNeedingReview = 0;
        var squareMetres = database.Insunits == UnitsValue.Undefined
            ? (double?)null
            : Math.Pow(UnitsConverter.GetConversionFactor(database.Insunits, UnitsValue.Meters), 2);
        for (var index = 0; index < detection.Rooms.Count; index++)
        {
            var room = detection.Rooms[index];
            var roomLabels = RoomLabels(scan, room);
            foreach (var label in roomLabels)
            {
                assignedLabels.Add(label.Handle);
            }
            var roomBlocks = scan.Blocks
                .Where(block => FloorPlanGeometry.Contains(room, block.Point))
                .ToList();

            var warnings = RoomWarnings(roomLabels);
            if (warnings.Count > 0)
            {
                roomsNeedingReview++;
            }

            rooms.Add(new
            {
                id = $"R{index + 1}",
                status = warnings.Count == 0 ? "ok" : "needs_review",
                name = roomLabels.Count == 1 ? roomLabels[0].Text : null,
                area = room.NetArea * factor * factor,
                grossArea = room.Area * factor * factor,
                areaSquareMetres = squareMetres is double toSquareMetres ? room.NetArea * toSquareMetres : (double?)null,
                perimeter = room.Perimeter * factor,
                centroid = new { x = room.Centroid.X * factor, y = room.Centroid.Y * factor },
                holes = room.Holes.Count,
                vertexCount = room.Boundary.Count,
                vertices = room.Boundary
                    .Take(MaxRoomVertices)
                    .Select(point => new { x = point.X * factor, y = point.Y * factor })
                    .ToList(),
                labels = roomLabels
                    .Select(label => new
                    {
                        text = label.Text,
                        handle = label.Handle,
                        point = new { x = label.Point.X * factor, y = label.Point.Y * factor }
                    })
                    .ToList(),
                blockCounts = roomBlocks
                    .GroupBy(block => block.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count()),
                blocks = roomBlocks
                    .Take(MaxRoomBlocks)
                    .Select(block => new { name = block.Name, handle = block.Handle })
                    .ToList(),
                warnings
            });
        }

        transaction.Commit();
        return Success(
            $"Found {detection.Rooms.Count} rooms ({roomsNeedingReview} need review), closed {detection.Closed.Count} gaps, and flagged {detection.Gaps.Count} open wall ends.",
            new
            {
                // Results with open wall ends or rooms needing review are not
                // confirmed; a designer must check them first.
                status = roomsNeedingReview == 0 && detection.Gaps.Count == 0 ? "ok" : "needs_review",
                roomsNeedingReview,
                units = units.Label,
                areaUnits = $"{units.Label}²",
                wallSegments = scan.Walls.Count,
                ignoredOnWallLayers = scan.Ignored,
                rooms,
                openWallEnds = OpenWallEnds(detection, factor),
                closedGaps = detection.Closed
                    .Select(gap => new
                    {
                        kind = gap.Kind,
                        start = new { x = gap.Start.X * factor, y = gap.Start.Y * factor },
                        end = new { x = gap.End.X * factor, y = gap.End.Y * factor },
                        width = gap.Width * factor
                    })
                    .ToList(),
                unassignedLabels = scan.Labels
                    .Where(label => !assignedLabels.Contains(label.Handle))
                    .Select(label => new { text = label.Text, handle = label.Handle })
                    .ToList()
            }
        );
    }

    private sealed record RoomLabel(string Text, string Handle, Vec Point);

    private sealed record RoomScan(
        List<WallSegment> Walls,
        Dictionary<string, string> WallLayerByHandle,
        List<RoomLabel> Labels,
        List<(string Name, string Handle, Vec Point)> Blocks,
        SortedDictionary<string, int> Ignored,
        double Tolerance,
        RoomDetection Detection
    );

    // Reads the walls, labels and blocks of model space and runs room
    // detection with the caller's sizes. Shared by detect_rooms and save_rooms
    // so both see exactly the same rooms.
    private static RoomScan ScanRooms(
        JsonElement arguments,
        Database database,
        Transaction transaction,
        UnitContext units
    )
    {
        var wallLayers = ReadNames(arguments, "wallLayers", required: true);
        var labelLayers = ReadNames(arguments, "labelLayers", required: false);
        var maxOpeningWidth = LengthOrDefault(arguments, "maxOpeningWidth", units.Scale, database, 1.2);
        var maxDraftingGap = LengthOrDefault(arguments, "maxDraftingGap", units.Scale, database, 0.02);
        var minRoomWidth = LengthOrDefault(arguments, "minRoomWidth", units.Scale, database, 0.5);
        var minRoomArea = HasValue(arguments, "minRoomArea")
            ? PositiveNumber(arguments, "minRoomArea") * units.Scale * units.Scale
            : Math.Pow(MetresToDrawing(database), 2);

        var modelSpace = ReadModelSpace(database, transaction);
        var walls = new List<WallSegment>();
        var wallLayerByHandle = new Dictionary<string, string>();
        var labels = new List<RoomLabel>();
        var blocks = new List<(string Name, string Handle, Vec Point)>();
        var ignored = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Extents3d? wallExtents = null;
        foreach (ObjectId id in modelSpace)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (wallLayers.Contains(entity.Layer))
            {
                if (!AddWallSegments(entity, walls))
                {
                    Count(ignored, id.ObjectClass.DxfName);
                }
                else
                {
                    wallLayerByHandle[entity.Handle.ToString()] = entity.Layer;
                    if (entity.Bounds is Extents3d box)
                    {
                        if (wallExtents is Extents3d current)
                        {
                            current.AddExtents(box);
                            wallExtents = current;
                        }
                        else
                        {
                            wallExtents = box;
                        }
                    }
                }
                if (walls.Count > MaxWallSegments)
                {
                    throw new ArgumentException(
                        $"The wall layers contain more than {MaxWallSegments} wall segments. "
                        + "Pass only the layers that hold walls and room separation lines."
                    );
                }
                continue;
            }

            if (entity.Bounds is not Extents3d bounds)
            {
                continue;
            }
            var center = new Vec(
                (bounds.MinPoint.X + bounds.MaxPoint.X) / 2,
                (bounds.MinPoint.Y + bounds.MaxPoint.Y) / 2
            );
            var handle = entity.Handle.ToString();
            var onLabelLayer = labelLayers.Count == 0 || labelLayers.Contains(entity.Layer);
            switch (entity)
            {
                case MText mtext when onLabelLayer && !string.IsNullOrWhiteSpace(mtext.Text):
                    labels.Add(new RoomLabel(mtext.Text.Trim(), handle, center));
                    break;
                case DBText text when onLabelLayer && !string.IsNullOrWhiteSpace(text.TextString):
                    labels.Add(new RoomLabel(text.TextString.Trim(), handle, center));
                    break;
                case BlockReference reference:
                    blocks.Add((BlockName(reference), handle, center));
                    if (onLabelLayer && RoomNameAttribute(reference) is string name)
                    {
                        labels.Add(new RoomLabel(name, handle, center));
                    }
                    break;
            }
        }

        if (walls.Count == 0)
        {
            throw new ArgumentException(
                $"No lines, polylines, arcs or circles were found on {string.Join(", ", wallLayers)}. "
                + "Use floor_plan_summary to see which layers hold the walls."
            );
        }

        // Points closer than this are treated as one: a millionth of the plan
        // size, so it scales with the drawing units.
        var size = wallExtents is Extents3d extents
            ? Math.Max(extents.MaxPoint.X - extents.MinPoint.X, extents.MaxPoint.Y - extents.MinPoint.Y)
            : 1;
        var tolerance = Math.Max(size * 1e-6, 1e-9);
        var detection = FloorPlanGeometry.DetectRooms(
            walls,
            tolerance,
            maxOpeningWidth,
            maxDraftingGap,
            minRoomArea,
            minRoomWidth
        );
        return new RoomScan(walls, wallLayerByHandle, labels, blocks, ignored, tolerance, detection);
    }

    private static List<RoomLabel> RoomLabels(RoomScan scan, RoomFace room) =>
        scan.Labels.Where(label => FloorPlanGeometry.Contains(room, label.Point)).ToList();

    // A closed shape holding several labels is never reported as one
    // confirmed room: it is usually two rooms joined through a doorway with
    // capped jambs.
    private static List<string> RoomWarnings(List<RoomLabel> roomLabels)
    {
        var warnings = new List<string>();
        if (roomLabels.Count > 1)
        {
            warnings.Add(
                "Several labels fall in this area, so it may be more than one room joined by an "
                + "opening that was not closed. Draw a room separation line across the opening on a "
                + "wall layer, between the label points, and run detect_rooms again."
            );
        }

        return warnings;
    }

    private static List<object> OpenWallEnds(RoomDetection detection, double factor) =>
        detection.Gaps
            .Select(gap => (object)new { x = gap.Point.X * factor, y = gap.Point.Y * factor, handle = gap.Source })
            .ToList();

    // Runs room detection and stores every room in the drawing's Named
    // Objects Dictionary. Rooms that match saved ones keep their IDs; saved
    // rooms no longer found are removed. One transaction, so UNDO reverts it.
    private static string SaveRooms(JsonElement arguments)
    {
        var document = RequireDocument();
        var database = document.Database;
        var units = Units(arguments, database);
        var factor = 1 / units.Scale;
        var confirmIds = ReadRoomIds(arguments, "confirmRoomIds");

        using var documentLock = document.LockDocument();
        using var transaction = database.TransactionManager.StartTransaction();
        var scan = ScanRooms(arguments, database, transaction, units);
        var detection = scan.Detection;
        var dictionary = RoomDictionary(database, transaction, OpenMode.ForWrite)!;
        var saved = ReadRoomRecords(dictionary, transaction);
        var nextId = ReadNextRoomId(dictionary, transaction, saved);

        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var drawingUnits = DrawingUnitsLabel(database);
        var metresPerUnit = database.Insunits == UnitsValue.Undefined
            ? (double?)null
            : UnitsConverter.GetConversionFactor(database.Insunits, UnitsValue.Meters);
        var fingerprints = scan.Walls
            .GroupBy(wall => wall.Source)
            .ToDictionary(
                group => group.Key,
                group => RoomRecords.Fingerprint(scan.WallLayerByHandle[group.Key], group)
            );

        var detected = detection.Rooms
            .Select(room =>
            {
                var roomLabels = RoomLabels(scan, room);
                var warnings = RoomWarnings(roomLabels);
                return new RoomRecord
                {
                    Name = roomLabels.Count == 1 ? roomLabels[0].Text : null,
                    Status = warnings.Count == 0 ? "ok" : "needs_review",
                    Warnings = warnings,
                    Boundary = RoomRecords.ToPoints(room.Boundary),
                    Holes = room.Holes.Select(RoomRecords.ToPoints).ToList(),
                    NetArea = room.NetArea,
                    GrossArea = room.Area,
                    Perimeter = room.Perimeter,
                    Units = drawingUnits,
                    MetresPerUnit = metresPerUnit,
                    SourceWalls = RoomRecords.SourceWallHandles(room, scan.Walls, scan.Tolerance)
                        .Select(handle => new SourceWall(handle, fingerprints[handle]))
                        .ToList(),
                    LabelHandles = roomLabels.Select(label => label.Handle).ToList(),
                    CreatedAt = now,
                    UpdatedAt = now
                };
            })
            .ToList();
        if (detected.Any(room => room.Boundary.Count > MaxRoomVertices))
        {
            return Error(
                $"A detected room has more than {MaxRoomVertices} outline vertices; nothing was saved. "
                + "Check the wall layers with detect_rooms."
            );
        }

        var ids = RoomRecords.Match(
            saved.Select(room => (room.Id, room.Name, (IReadOnlyList<Vec>)RoomRecords.ToVecs(room.Boundary))).ToList(),
            detected.Select(room => (room.Name, (IReadOnlyList<Vec>)RoomRecords.ToVecs(room.Boundary))).ToList()
        );
        var savedById = saved.ToDictionary(room => room.Id);
        var kept = 0;
        for (var index = 0; index < detected.Count; index++)
        {
            var room = detected[index];
            if (ids[index] is string id)
            {
                var previous = savedById[id];
                room.Id = id;
                room.CreatedAt = previous.CreatedAt;
                if (RoomRecords.KeepsConfirmation(previous, room))
                {
                    room.Status = "confirmed";
                }
                kept++;
            }
            else
            {
                room.Id = $"R{nextId++}";
            }
        }

        // Only a room detected cleanly, in a plan with no open wall ends, can
        // be confirmed; otherwise nothing is saved.
        var byId = detected.ToDictionary(room => room.Id);
        foreach (var id in confirmIds)
        {
            if (!byId.TryGetValue(id, out var room))
            {
                return Error(
                    $"Room {id} is not among the detected rooms ({string.Join(", ", byId.Keys)}); nothing was saved. "
                    + "Call get_rooms to see the saved IDs."
                );
            }
            if (room.Status == "needs_review")
            {
                return Error($"Room {id} needs review and cannot be confirmed; nothing was saved. {string.Join(" ", room.Warnings)}");
            }
            if (detection.Gaps.Count > 0)
            {
                return Error(
                    $"The plan has {detection.Gaps.Count} open wall ends, so no room can be confirmed; nothing was saved. "
                    + "Close the walls or confirm with the user that the open ends are intended, fix them, and save again."
                );
            }
            room.Status = "confirmed";
        }

        var removed = saved.Where(room => !byId.ContainsKey(room.Id)).Select(room => room.Id).ToList();
        foreach (var id in removed)
        {
            var objectId = dictionary.GetAt(id);
            dictionary.Remove(objectId);
            transaction.GetObject(objectId, OpenMode.ForWrite).Erase();
        }
        foreach (var room in detected)
        {
            WriteXrecord(dictionary, transaction, room.Id, RoomRecords.Serialize(room));
        }
        WriteXrecord(dictionary, transaction, RoomsMetaKey, new List<string> { nextId.ToString(CultureInfo.InvariantCulture) });
        transaction.Commit();

        document.Editor.WriteMessage($"\nClaude saved {detected.Count} rooms in the drawing. Use UNDO to revert it.");
        var needsReview = detected.Count(room => room.Status == "needs_review");
        return Success(
            $"Saved {detected.Count} rooms ({kept} matched saved rooms and kept their IDs, {detected.Count - kept} new, "
            + $"{removed.Count} removed). {needsReview} need review and are saved with status needs_review.",
            new
            {
                status = needsReview == 0 && detection.Gaps.Count == 0 ? "ok" : "needs_review",
                units = units.Label,
                rooms = detected.Select(room => DescribeRoom(room, room.Status, new List<string>(), factor, false)).ToList(),
                removedRoomIds = removed,
                openWallEnds = OpenWallEnds(detection, factor)
            }
        );
    }

    // Returns the saved rooms, each checked against the drawing: a room whose
    // walls were erased, moved or changed is reported as stale.
    private static string GetRooms(JsonElement arguments)
    {
        var document = RequireDocument();
        var database = document.Database;
        var units = Units(arguments, database);
        var includeVertices = OptionalBoolean(arguments, "includeVertices", true);

        using var documentLock = document.LockDocument();
        using var transaction = database.TransactionManager.StartTransaction();
        var rooms = CheckedRooms(database, transaction);
        transaction.Commit();

        var stale = rooms.Count(room => room.Status == "stale");
        return Success(
            rooms.Count == 0
                ? "No rooms are saved in this drawing. Use save_rooms first."
                : $"{rooms.Count} saved rooms; {stale} stale.",
            new
            {
                units = units.Label,
                areaUnits = $"{units.Label}²",
                measurementConvention = RoomRecords.MeasurementConvention,
                rooms = rooms
                    .Select(room => DescribeRoom(room.Record, room.Status, room.StaleReasons, 1 / units.Scale, includeVertices))
                    .ToList()
            }
        );
    }

    // Writes the saved rooms as a CSV room schedule. Rooms are checked for
    // staleness first and their status is written as it is: needs_review and
    // stale rooms are never shown as confirmed.
    private static string ExportRoomSchedule(JsonElement arguments)
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
                Path.GetFileNameWithoutExtension(database.Filename) + "-rooms.csv"
            );
        }
        else if (!Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            return Error("path must be a full Windows path ending in .csv.");
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

        using var documentLock = document.LockDocument();
        using var transaction = database.TransactionManager.StartTransaction();
        var rooms = CheckedRooms(database, transaction);
        transaction.Commit();
        if (rooms.Count == 0)
        {
            return Error("No rooms are saved in this drawing. Use save_rooms first.");
        }
        if (rooms.Any(room => room.Record.MetresPerUnit is null))
        {
            return Error(
                "Some rooms were saved in a unitless drawing (INSUNITS = 0), so areas cannot be given in m². "
                + "Set INSUNITS and save the rooms again."
            );
        }

        var rows = rooms
            .Select(room =>
            {
                var metres = room.Record.MetresPerUnit!.Value;
                return new ScheduleRow(
                    room.Record.Id,
                    room.Record.Name,
                    room.Status,
                    room.Record.NetArea * metres * metres,
                    room.Record.GrossArea * metres * metres,
                    room.Record.Perimeter * metres,
                    room.Record.Warnings.Concat(room.StaleReasons).ToList()
                );
            })
            .ToList();
        File.WriteAllText(path, RoomRecords.ScheduleCsv(rows), new UTF8Encoding(true));

        var statuses = rows
            .GroupBy(row => row.Status)
            .ToDictionary(group => group.Key, group => group.Count());
        var unconfirmed = rows.Count(row => row.Status != "confirmed");
        return Success(
            $"Wrote {rows.Count} rooms to {path}. {unconfirmed} are not confirmed; their status is in the file.",
            new { path, rows = rows.Count, statuses }
        );
    }

    private sealed record CheckedRoom(RoomRecord Record, string Status, List<string> StaleReasons);

    private static List<CheckedRoom> CheckedRooms(Database database, Transaction transaction)
    {
        var dictionary = RoomDictionary(database, transaction, OpenMode.ForRead);
        if (dictionary is null)
        {
            return new List<CheckedRoom>();
        }

        var drawingUnits = DrawingUnitsLabel(database);
        var current = new Dictionary<string, string?>();
        var rooms = new List<CheckedRoom>();
        foreach (var record in ReadRoomRecords(dictionary, transaction))
        {
            foreach (var wall in record.SourceWalls)
            {
                if (!current.ContainsKey(wall.Handle))
                {
                    current[wall.Handle] = CurrentFingerprint(database, transaction, wall.Handle);
                }
            }

            var reasons = RoomRecords.StaleReasons(record.SourceWalls, current);
            if (record.Units != drawingUnits)
            {
                reasons.Insert(0, $"The drawing units changed from {record.Units} to {drawingUnits}.");
            }
            rooms.Add(new CheckedRoom(record, reasons.Count > 0 ? "stale" : record.Status, reasons));
        }

        return rooms
            .OrderBy(room => int.TryParse(room.Record.Id.AsSpan(1), out var number) ? number : int.MaxValue)
            .ToList();
    }

    private static string? CurrentFingerprint(Database database, Transaction transaction, string handle)
    {
        if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            || !database.TryGetObjectId(new Handle(value), out var id)
            || id.IsErased
            || transaction.GetObject(id, OpenMode.ForRead) is not Entity entity
            || entity.OwnerId != SymbolUtilityServices.GetBlockModelSpaceId(database))
        {
            return null;
        }

        var segments = new List<WallSegment>();
        return AddWallSegments(entity, segments) ? RoomRecords.Fingerprint(entity.Layer, segments) : null;
    }

    private static object DescribeRoom(
        RoomRecord room,
        string status,
        List<string> staleReasons,
        double factor,
        bool includeVertices
    )
    {
        var metres = room.MetresPerUnit;
        return new
        {
            id = room.Id,
            name = room.Name,
            status,
            savedStatus = room.Status,
            staleReasons,
            warnings = room.Warnings,
            netArea = room.NetArea * factor * factor,
            grossArea = room.GrossArea * factor * factor,
            perimeter = room.Perimeter * factor,
            netAreaSquareMetres = metres is double m ? room.NetArea * m * m : (double?)null,
            grossAreaSquareMetres = metres is double g ? room.GrossArea * g * g : (double?)null,
            perimeterMetres = metres is double p ? room.Perimeter * p : (double?)null,
            drawingUnits = room.Units,
            holes = room.Holes.Count,
            vertices = includeVertices
                ? room.Boundary.Select(point => new { x = point[0] * factor, y = point[1] * factor }).ToList()
                : null,
            sourceWallHandles = room.SourceWalls.Select(wall => wall.Handle).ToList(),
            labelHandles = room.LabelHandles,
            detectionVersion = room.DetectionVersion,
            createdAt = room.CreatedAt,
            updatedAt = room.UpdatedAt
        };
    }

    // The rooms sub-dictionary of the Named Objects Dictionary; null when
    // reading a drawing that has none. Opened for write, it is created.
    private static DBDictionary? RoomDictionary(Database database, Transaction transaction, OpenMode mode)
    {
        var named = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (named.Contains(RoomsKey))
        {
            return transaction.GetObject(named.GetAt(RoomsKey), mode) as DBDictionary
                ?? throw new InvalidOperationException($"{RoomsKey} in this drawing is not a bridge dictionary; nothing was changed.");
        }
        if (mode == OpenMode.ForRead)
        {
            return null;
        }

        named.UpgradeOpen();
        var dictionary = new DBDictionary();
        named.SetAt(RoomsKey, dictionary);
        transaction.AddNewlyCreatedDBObject(dictionary, true);
        return dictionary;
    }

    private static List<RoomRecord> ReadRoomRecords(DBDictionary dictionary, Transaction transaction)
    {
        var rooms = new List<RoomRecord>();
        foreach (DBDictionaryEntry entry in dictionary)
        {
            if (entry.Key == RoomsMetaKey)
            {
                continue;
            }

            try
            {
                var room = RoomRecords.Deserialize(XrecordText(transaction, entry.Value));
                if (room.Id != entry.Key)
                {
                    throw new FormatException("The record ID does not match its key.");
                }
                rooms.Add(room);
            }
            catch (System.Exception exception) when (exception is FormatException or JsonException)
            {
                throw new InvalidOperationException(
                    $"Saved room {entry.Key} in {RoomsKey} cannot be read ({exception.Message}); nothing was changed."
                );
            }
        }

        return rooms;
    }

    // IDs are never reused, so the next number is kept even after rooms are
    // removed.
    private static int ReadNextRoomId(DBDictionary dictionary, Transaction transaction, List<RoomRecord> saved)
    {
        var next = 1;
        if (dictionary.Contains(RoomsMetaKey)
            && int.TryParse(
                XrecordText(transaction, dictionary.GetAt(RoomsMetaKey)).FirstOrDefault(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var stored))
        {
            next = stored;
        }
        foreach (var room in saved)
        {
            if (int.TryParse(room.Id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                next = Math.Max(next, number + 1);
            }
        }

        return next;
    }

    private static List<string> XrecordText(Transaction transaction, ObjectId id)
    {
        if (transaction.GetObject(id, OpenMode.ForRead) is not Xrecord record)
        {
            throw new FormatException("It is not an Xrecord.");
        }

        using var data = record.Data;
        return (data?.AsArray() ?? Array.Empty<TypedValue>())
            .Where(value => value.TypeCode == (short)DxfCode.Text)
            .Select(value => (string)value.Value)
            .ToList();
    }

    private static void WriteXrecord(DBDictionary dictionary, Transaction transaction, string key, List<string> chunks)
    {
        using var data = new ResultBuffer(
            chunks.Select(chunk => new TypedValue((int)DxfCode.Text, chunk)).ToArray()
        );
        if (dictionary.Contains(key))
        {
            var existing = (Xrecord)transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite);
            existing.Data = data;
            return;
        }

        using var record = new Xrecord { Data = data };
        dictionary.SetAt(key, record);
        transaction.AddNewlyCreatedDBObject(record, true);
    }

    private static List<string> ReadRoomIds(JsonElement arguments, string name)
    {
        var ids = new List<string>();
        if (!HasValue(arguments, name))
        {
            return ids;
        }

        var array = arguments.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxHandles)
        {
            throw new ArgumentException($"{name} must be an array of at most {MaxHandles} room IDs.");
        }
        foreach (var element in array.EnumerateArray())
        {
            var id = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
            if (id.Length is < 2 or > 8 || id[0] != 'R' || !id.Skip(1).All(char.IsAsciiDigit))
            {
                throw new ArgumentException($"Every entry in {name} must be a room ID such as R1.");
            }
            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    // Adds the straight pieces of one wall entity; arcs and circles become
    // short chords. Returns false for entity types that are not walls.
    private static bool AddWallSegments(Entity entity, List<WallSegment> walls)
    {
        var source = entity.Handle.ToString();
        switch (entity)
        {
            case Line line:
                walls.Add(new WallSegment(ToVec(line.StartPoint), ToVec(line.EndPoint), source));
                return true;
            case Polyline polyline:
                var count = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
                for (var index = 0; index < count; index++)
                {
                    if (polyline.GetSegmentType(index) == SegmentType.Arc)
                    {
                        // A bulge is tan(sweep / 4), positive counter-clockwise.
                        var bend = polyline.GetArcSegment2dAt(index);
                        var start = polyline.GetPoint2dAt(index);
                        AddArcChords(
                            new Vec(bend.Center.X, bend.Center.Y),
                            bend.Radius,
                            Math.Atan2(start.Y - bend.Center.Y, start.X - bend.Center.X),
                            4 * Math.Atan(polyline.GetBulgeAt(index)),
                            source,
                            walls
                        );
                    }
                    else if (polyline.GetSegmentType(index) == SegmentType.Line)
                    {
                        var segment = polyline.GetLineSegment2dAt(index);
                        walls.Add(new WallSegment(
                            new Vec(segment.StartPoint.X, segment.StartPoint.Y),
                            new Vec(segment.EndPoint.X, segment.EndPoint.Y),
                            source
                        ));
                    }
                }
                return true;
            case Arc arc:
                var sweep = arc.EndAngle - arc.StartAngle;
                AddArcChords(
                    ToVec(arc.Center),
                    arc.Radius,
                    arc.StartAngle,
                    sweep <= 0 ? sweep + 2 * Math.PI : sweep,
                    source,
                    walls
                );
                return true;
            case Circle circle:
                AddArcChords(ToVec(circle.Center), circle.Radius, 0, 2 * Math.PI, source, walls);
                return true;
            default:
                return false;
        }
    }

    // About one chord per 10 degrees of arc.
    private static void AddArcChords(
        Vec center,
        double radius,
        double startAngle,
        double sweep,
        string source,
        List<WallSegment> walls
    )
    {
        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 18)), 1, 36);
        var previous = center + new Vec(Math.Cos(startAngle), Math.Sin(startAngle)) * radius;
        for (var step = 1; step <= steps; step++)
        {
            var angle = startAngle + sweep * step / steps;
            var next = center + new Vec(Math.Cos(angle), Math.Sin(angle)) * radius;
            walls.Add(new WallSegment(previous, next, source));
            previous = next;
        }
    }

    private static Vec ToVec(Point3d point) => new(point.X, point.Y);

    private static string? RoomNameAttribute(BlockReference reference)
    {
        foreach (ObjectId id in reference.AttributeCollection)
        {
            if (!id.IsErased
                && id.GetObject(OpenMode.ForRead) is AttributeReference attribute
                && RoomNameTags.Contains(attribute.Tag, StringComparer.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(attribute.TextString))
            {
                return attribute.TextString.Trim();
            }
        }

        return null;
    }

    private static HashSet<string> ReadNames(JsonElement arguments, string name, bool required)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!HasValue(arguments, name))
        {
            return required ? throw new ArgumentException($"{name} is required.") : names;
        }

        var array = arguments.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() is < 1 or > MaxLayerNames)
        {
            throw new ArgumentException($"{name} must be an array of 1 to {MaxLayerNames} layer names.");
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"Every entry in {name} must be a layer name.");
            }

            names.Add(ValidName(element.GetString() ?? string.Empty, name));
        }

        return names;
    }

    // A caller-supplied length, or a default given in metres and converted to
    // drawing units. Unitless drawings have no safe default.
    private static double LengthOrDefault(
        JsonElement arguments,
        string name,
        double scale,
        Database database,
        double metres
    )
    {
        return HasValue(arguments, name)
            ? PositiveLength(arguments, name, scale)
            : metres * MetresToDrawing(database);
    }

    private static double MetresToDrawing(Database database)
    {
        if (database.Insunits == UnitsValue.Undefined)
        {
            throw new ArgumentException(
                "The drawing is unitless (INSUNITS = 0), so room detection has no default sizes. "
                + "Pass maxOpeningWidth, maxDraftingGap, minRoomWidth and minRoomArea in drawing units, "
                + "or set INSUNITS in AutoCAD."
            );
        }

        return UnitsConverter.GetConversionFactor(UnitsValue.Meters, database.Insunits);
    }

    private static string? ReadPlanType(Database database, Transaction transaction)
    {
        var dictionary = (DBDictionary)transaction.GetObject(
            database.NamedObjectsDictionaryId,
            OpenMode.ForRead
        );
        if (!dictionary.Contains(FloorPlanKey)
            || transaction.GetObject(dictionary.GetAt(FloorPlanKey), OpenMode.ForRead) is not Xrecord record)
        {
            return null;
        }

        using var data = record.Data;
        var values = data?.AsArray();
        return values is { Length: > 0 } && values[0].Value is string planType && PlanTypes.Contains(planType)
            ? planType
            : null;
    }

    private static void Count(SortedDictionary<string, int> counts, string key)
    {
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
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
            BlockReference reference => DescribeBlock(reference, factor),
            _ => null
        };
    }

    // Furniture, fixtures, doors and symbols in a floor plan are usually
    // blocks, so report the definition name, placement and attribute values.
    // Must run inside an open transaction.
    private static object DescribeBlock(BlockReference reference, double factor)
    {
        var attributes = new Dictionary<string, string>();
        foreach (ObjectId id in reference.AttributeCollection)
        {
            if (!id.IsErased && id.GetObject(OpenMode.ForRead) is AttributeReference attribute)
            {
                attributes[attribute.Tag] = attribute.TextString;
            }
        }

        return new
        {
            name = BlockName(reference),
            position = Xy(reference.Position, factor),
            rotation = RadiansToDegrees(reference.Rotation),
            scale = new { x = reference.ScaleFactors.X, y = reference.ScaleFactors.Y },
            attributes
        };
    }

    // Dynamic blocks are stored as anonymous *U definitions; report the
    // name the user sees instead.
    private static string BlockName(BlockReference reference)
    {
        var definitionId = reference.IsDynamicBlock
            ? reference.DynamicBlockTableRecord
            : reference.BlockTableRecord;
        return ((BlockTableRecord)definitionId.GetObject(OpenMode.ForRead)).Name;
    }

    private static object EntityEntry(ObjectId id, Entity entity, double factor)
    {
        return new
        {
            handle = entity.Handle.ToString(),
            type = id.ObjectClass.DxfName,
            kind = entity.GetType().Name,
            layer = entity.Layer,
            colorIndex = entity.ColorIndex,
            bounds = Bounds(entity, factor),
            geometry = Describe(entity, factor)
        };
    }

    private static object? Bounds(Entity entity, double factor)
    {
        return entity.Bounds is Extents3d box
            ? new { min = Xy(box.MinPoint, factor), max = Xy(box.MaxPoint, factor) }
            : null;
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

    private static BlockTableRecord ReadModelSpace(Database database, Transaction transaction)
    {
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        return (BlockTableRecord)transaction.GetObject(
            blockTable[BlockTableRecord.ModelSpace],
            OpenMode.ForRead
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
        return value is null ? null : ValidName(value, label);
    }

    private static string ValidName(string value, string label)
    {
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
