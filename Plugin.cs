using System;
using System.Collections.Concurrent;
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
            _ => Error("Unknown or disabled AutoCAD tool.")
        };
    }

    private static string Status()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        return document is null
            ? Error("AutoCAD is running, but no drawing is open.")
            : Success(
                "AutoCAD is connected.",
                new { drawing = document.Name, port = Port }
            );
    }

    private static string DrawLine(JsonElement arguments)
    {
        var startX = RequiredNumber(arguments, "startX");
        var startY = RequiredNumber(arguments, "startY");
        var endX = RequiredNumber(arguments, "endX");
        var endY = RequiredNumber(arguments, "endY");
        var layerName = LayerName(arguments);
        var document = RequireDocument();

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        EnsureLayer(document.Database, transaction, layerName);
        var modelSpace = OpenModelSpace(document.Database, transaction);

        using var line = new Line(
            new Point3d(startX, startY, 0),
            new Point3d(endX, endY, 0)
        )
        {
            Layer = layerName
        };
        modelSpace.AppendEntity(line);
        transaction.AddNewlyCreatedDBObject(line, true);
        var handle = line.Handle.ToString();
        transaction.Commit();

        document.Editor.WriteMessage("\nClaude created one line. Use UNDO to remove it.");
        return Success("Line created.", new { handle, layer = layerName });
    }

    private static string DrawRectangle(JsonElement arguments)
    {
        var x = RequiredNumber(arguments, "x");
        var y = RequiredNumber(arguments, "y");
        var width = PositiveNumber(arguments, "width");
        var height = PositiveNumber(arguments, "height");
        var layerName = LayerName(arguments);
        var document = RequireDocument();

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
            new { handle, layer = layerName, x, y, width, height }
        );
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
            || Math.Abs(value) > 1_000_000_000)
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

    private static string Success(string message, object data)
    {
        return JsonSerializer.Serialize(new { ok = true, message, data });
    }

    private static string Error(string message)
    {
        return JsonSerializer.Serialize(new { ok = false, error = message });
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
