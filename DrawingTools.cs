using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADClaudeBridge;

// Floor plan drafting tools: arcs and circles, double-line walls with doors
// and windows, furniture symbols, blocks, and copy/rotate/mirror by handle.
internal static partial class BridgeHost
{
    private const string WallLayerDefault = "A-WALL";
    private const string DoorLayerDefault = "A-DOOR";
    private const string WindowLayerDefault = "A-GLAZ";
    private const string SeparationLayerDefault = "A-ROOM-SEP";
    private const string FurnitureLayer = "A-FURN";
    private const string FixtureLayer = "A-FIXT";
    private const string FurniturePrefix = "CB_";
    private const int MaxOpenings = 200;
    private const int MaxCopies = 100;
    private const int MaxAttributes = 50;
    private static readonly string[] OpeningKinds = { WallGeometry.Door, WallGeometry.Window, WallGeometry.Opening };

    // Blocks Claude may insert by name when they are not already defined in
    // the drawing: one DWG per block, so no other file can be reached.
    private static string BlockLibraryFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoCADClaudeBridge",
        "blocks"
    );

    private static string DrawCircle(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var center = Point(arguments, "centerX", "centerY", units.Scale);
        var radius = PositiveSize(arguments, "radius", units.Scale);
        var layerName = LayerName(arguments);
        var color = ColorValue(arguments);

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        EnsureLayer(document.Database, transaction, layerName);
        var modelSpace = OpenModelSpace(document.Database, transaction);
        using var circle = new Circle(center, Vector3d.ZAxis, radius) { Layer = layerName, Color = color };
        modelSpace.AppendEntity(circle);
        transaction.AddNewlyCreatedDBObject(circle, true);
        var handle = circle.Handle.ToString();
        transaction.Commit();

        document.Editor.WriteMessage("\nClaude created one circle. Use UNDO to remove it.");
        return Success("Circle created.", new { handle, layer = layerName, radius = radius / units.Scale, units = units.Label });
    }

    private static string DrawArc(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var center = Point(arguments, "centerX", "centerY", units.Scale);
        var radius = PositiveSize(arguments, "radius", units.Scale);
        var startAngle = DegreesToRadians(OptionalAngle(arguments, "startAngle", double.NaN));
        var endAngle = DegreesToRadians(OptionalAngle(arguments, "endAngle", double.NaN));
        if (double.IsNaN(startAngle) || double.IsNaN(endAngle))
        {
            throw new ArgumentException("startAngle and endAngle are required.");
        }
        if (Math.Abs(startAngle - endAngle) <= Tolerance)
        {
            throw new ArgumentException("startAngle and endAngle must differ.");
        }
        var layerName = LayerName(arguments);
        var color = ColorValue(arguments);

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        EnsureLayer(document.Database, transaction, layerName);
        var modelSpace = OpenModelSpace(document.Database, transaction);
        using var arc = new Arc(center, radius, startAngle, endAngle) { Layer = layerName, Color = color };
        modelSpace.AppendEntity(arc);
        transaction.AddNewlyCreatedDBObject(arc, true);
        var handle = arc.Handle.ToString();
        var length = arc.Length / units.Scale;
        transaction.Commit();

        document.Editor.WriteMessage("\nClaude created one arc. Use UNDO to remove it.");
        return Success("Arc created.", new { handle, layer = layerName, length, units = units.Label });
    }

    private static string DrawWalls(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var path = ReadPoints(arguments, "points", 2, units.Scale)
            .Select(point => new Vec(point.X, point.Y))
            .ToList();
        var closed = OptionalBoolean(arguments, "closed", false);
        var thickness = PositiveSize(arguments, "thickness", units.Scale);
        var capEnds = OptionalBoolean(arguments, "capEnds", true);
        var openings = ReadOpenings(arguments, units.Scale);
        var layers = new Dictionary<PlanRole, string>
        {
            [PlanRole.Wall] = OptionalName(arguments, "wallLayer", "wallLayer") ?? WallLayerDefault,
            [PlanRole.Door] = OptionalName(arguments, "doorLayer", "doorLayer") ?? DoorLayerDefault,
            [PlanRole.Window] = OptionalName(arguments, "windowLayer", "windowLayer") ?? WindowLayerDefault,
            [PlanRole.Separation] = OptionalName(arguments, "separationLayer", "separationLayer") ?? SeparationLayerDefault
        };
        var drawing = WallGeometry.Build(path, closed, thickness, openings, capEnds);

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        foreach (var layerName in layers.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            EnsureLayer(document.Database, transaction, layerName);
        }
        var modelSpace = OpenModelSpace(document.Database, transaction);

        var handles = layers.Keys.ToDictionary(role => role, _ => new List<string>());
        foreach (var planLine in drawing.Lines)
        {
            using var line = new Line(ToPoint(planLine.Start), ToPoint(planLine.End)) { Layer = layers[planLine.Role] };
            modelSpace.AppendEntity(line);
            transaction.AddNewlyCreatedDBObject(line, true);
            handles[planLine.Role].Add(line.Handle.ToString());
        }
        foreach (var planArc in drawing.Arcs)
        {
            using var arc = new Arc(ToPoint(planArc.Center), planArc.Radius, planArc.StartAngle, planArc.EndAngle)
            {
                Layer = layers[planArc.Role]
            };
            modelSpace.AppendEntity(arc);
            transaction.AddNewlyCreatedDBObject(arc, true);
            handles[planArc.Role].Add(arc.Handle.ToString());
        }
        transaction.Commit();

        document.Editor.WriteMessage(
            $"\nClaude drew walls with {openings.Count} openings. Use UNDO to remove them."
        );
        return Success(
            $"Walls drawn: {handles[PlanRole.Wall].Count} wall lines, {openings.Count} openings.",
            new
            {
                wallLayer = layers[PlanRole.Wall],
                separationLayer = layers[PlanRole.Separation],
                wallHandles = handles[PlanRole.Wall],
                doorHandles = handles[PlanRole.Door],
                windowHandles = handles[PlanRole.Window],
                separationHandles = handles[PlanRole.Separation],
                units = units.Label
            }
        );
    }

    private static List<WallOpening> ReadOpenings(JsonElement arguments, double scale)
    {
        var openings = new List<WallOpening>();
        if (!HasValue(arguments, "openings"))
        {
            return openings;
        }

        var array = arguments.GetProperty("openings");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxOpenings)
        {
            throw new ArgumentException($"openings must be an array of at most {MaxOpenings} openings.");
        }

        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var label = $"openings[{index++}]";
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException($"{label} must be an object.");
            }

            try
            {
                var kind = Choice(element, "kind", OpeningKinds, WallGeometry.Door);
                openings.Add(new WallOpening(
                    OptionalInteger(element, "segment", -1, 0, MaxPoints),
                    OptionalNonNegative(element, "offset", scale)
                        ?? throw new ArgumentException("offset is required."),
                    PositiveSize(element, "width", scale),
                    kind,
                    Choice(element, "hinge", new[] { "start", "end" }, "start"),
                    Choice(element, "swing", new[] { "left", "right" }, "left")
                ));
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"{label}: {exception.Message}");
            }
            if (openings[^1].Segment < 0)
            {
                throw new ArgumentException($"{label}: segment is required.");
            }
        }

        return openings;
    }

    private static string Choice(JsonElement arguments, string name, string[] choices, string fallback)
    {
        var value = OptionalText(arguments, name) ?? fallback;
        return choices.FirstOrDefault(choice => string.Equals(choice, value, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"{name} must be one of: {string.Join(", ", choices)}.");
    }

    private static string PlaceFurniture(JsonElement arguments)
    {
        var document = RequireDocument();
        var database = document.Database;
        var units = Units(arguments, database);
        var kind = Choice(arguments, "kind", FurnitureCatalog.Kinds, "");
        var symbol = FurnitureCatalog.Get(kind);
        var position = Point(arguments, "x", "y", units.Scale);
        var rotation = DegreesToRadians(OptionalAngle(arguments, "rotation", 0));
        var metre = MetresToDrawing(database);
        var width = OptionalPositive(arguments, "width", units.Scale) ?? symbol.Width * metre;
        var depth = OptionalPositive(arguments, "depth", units.Scale) ?? symbol.Depth * metre;
        var layerName = OptionalName(arguments, "layer", "layer") ?? (symbol.Fixture ? FixtureLayer : FurnitureLayer);
        var blockName = FurniturePrefix + kind.ToUpperInvariant();

        using var documentLock = document.LockDocument();
        using var transaction = database.TransactionManager.StartTransaction();
        EnsureLayer(database, transaction, layerName);
        var definitionId = EnsureFurnitureBlock(database, transaction, blockName, symbol, metre);
        var modelSpace = OpenModelSpace(database, transaction);
        using var reference = new BlockReference(position, definitionId)
        {
            Rotation = rotation,
            ScaleFactors = new Scale3d(width / (symbol.Width * metre), depth / (symbol.Depth * metre), 1),
            Layer = layerName
        };
        modelSpace.AppendEntity(reference);
        transaction.AddNewlyCreatedDBObject(reference, true);
        var handle = reference.Handle.ToString();
        transaction.Commit();

        document.Editor.WriteMessage($"\nClaude placed one {kind}. Use UNDO to remove it.");
        return Success(
            $"Placed {kind}.",
            new
            {
                handle,
                block = blockName,
                layer = layerName,
                width = width / units.Scale,
                depth = depth / units.Scale,
                units = units.Label
            }
        );
    }

    // Furniture symbols are drawn once per drawing at their standard size,
    // centred on the block origin with the back edge at +Y, on layer 0 and
    // colour ByBlock so each placed copy follows its own layer.
    private static ObjectId EnsureFurnitureBlock(
        Database database,
        Transaction transaction,
        string blockName,
        FurnitureSymbol symbol,
        double metre
    )
    {
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        if (blockTable.Has(blockName))
        {
            return blockTable[blockName];
        }

        blockTable.UpgradeOpen();
        using var definition = new BlockTableRecord { Name = blockName, Origin = Point3d.Origin };
        var id = blockTable.Add(definition);
        transaction.AddNewlyCreatedDBObject(definition, true);
        foreach (var entity in symbol.Build(metre))
        {
            using (entity)
            {
                entity.Layer = "0";
                entity.Color = Color.FromColorIndex(ColorMethod.ByBlock, 0);
                definition.AppendEntity(entity);
                transaction.AddNewlyCreatedDBObject(entity, true);
            }
        }

        return id;
    }

    private static string InsertBlock(JsonElement arguments)
    {
        var document = RequireDocument();
        var database = document.Database;
        var units = Units(arguments, database);
        var name = OptionalName(arguments, "name", "name") ?? throw new ArgumentException("name is required.");
        var position = Point(arguments, "x", "y", units.Scale);
        var rotation = DegreesToRadians(OptionalAngle(arguments, "rotation", 0));
        var scale = OptionalPositiveRatio(arguments, "scale") ?? 1;
        var scaleX = OptionalPositiveRatio(arguments, "scaleX") ?? scale;
        var scaleY = OptionalPositiveRatio(arguments, "scaleY") ?? scale;
        var layerName = OptionalName(arguments, "layer", "layer") ?? FurnitureLayer;
        var values = ReadAttributeValues(arguments);

        using var documentLock = document.LockDocument();
        var imported = ImportLibraryBlock(database, name);

        using var transaction = database.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        if (!blockTable.Has(name))
        {
            var available = Directory.Exists(BlockLibraryFolder)
                ? Directory.GetFiles(BlockLibraryFolder, "*.dwg").Select(Path.GetFileNameWithoutExtension).Take(50).ToList()
                : new List<string?>();
            return Error(
                $"Block {name} is not defined in this drawing and is not in the block library ({BlockLibraryFolder}). "
                + (available.Count > 0 ? $"Library blocks: {string.Join(", ", available)}." : "The library is empty.")
            );
        }

        EnsureLayer(database, transaction, layerName);
        var definitionId = blockTable[name];
        var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
        if (definition.IsLayout || definition.IsAnonymous || definition.IsFromExternalReference)
        {
            return Error($"{name} is not an insertable block.");
        }

        var modelSpace = OpenModelSpace(database, transaction);
        using var reference = new BlockReference(position, definitionId)
        {
            Rotation = rotation,
            ScaleFactors = new Scale3d(scaleX, scaleY, 1),
            Layer = layerName
        };
        modelSpace.AppendEntity(reference);
        transaction.AddNewlyCreatedDBObject(reference, true);

        var unknownTags = new HashSet<string>(values.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (ObjectId id in definition)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not AttributeDefinition attributeDefinition
                || attributeDefinition.Constant)
            {
                continue;
            }

            using var attribute = new AttributeReference();
            attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
            if (values.TryGetValue(attributeDefinition.Tag, out var value))
            {
                attribute.TextString = value;
                unknownTags.Remove(attributeDefinition.Tag);
            }
            reference.AttributeCollection.AppendAttribute(attribute);
            transaction.AddNewlyCreatedDBObject(attribute, true);
        }
        if (unknownTags.Count > 0)
        {
            return Error($"Block {name} has no attributes {string.Join(", ", unknownTags)}; nothing was inserted.");
        }

        var handle = reference.Handle.ToString();
        transaction.Commit();

        document.Editor.WriteMessage($"\nClaude inserted block {name}. Use UNDO to remove it.");
        return Success(
            imported ? $"Imported {name} from the block library and inserted it." : $"Inserted {name}.",
            new { handle, block = name, layer = layerName, units = units.Label }
        );
    }

    // Copies <name>.dwg from the block library into the drawing as a block
    // definition when the drawing does not already define that block.
    private static bool ImportLibraryBlock(Database database, string name)
    {
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            if (blockTable.Has(name))
            {
                return false;
            }
        }

        var file = Path.Combine(BlockLibraryFolder, name + ".dwg");
        if (!File.Exists(file))
        {
            return false;
        }

        using var source = new Database(false, true);
        source.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
        database.Insert(name, source, true);
        return true;
    }

    private static Dictionary<string, string> ReadAttributeValues(JsonElement arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!HasValue(arguments, "attributes"))
        {
            return values;
        }

        var element = arguments.GetProperty("attributes");
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("attributes must be an object of tag: value pairs.");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (values.Count >= MaxAttributes)
            {
                throw new ArgumentException($"attributes may contain at most {MaxAttributes} tags.");
            }
            if (property.Value.ValueKind != JsonValueKind.String
                || property.Value.GetString()!.Length > MaxTextLength)
            {
                throw new ArgumentException($"Attribute {property.Name} must be text of at most {MaxTextLength} characters.");
            }
            values[ValidName(property.Name, "attribute tag")] = property.Value.GetString()!;
        }

        return values;
    }

    // Scale factors are ratios, never unit-converted.
    private static double? OptionalPositiveRatio(JsonElement arguments, string name)
    {
        if (!HasValue(arguments, name))
        {
            return null;
        }

        var value = PositiveNumber(arguments, name);
        if (value > 1000)
        {
            throw new ArgumentException($"{name} must not exceed 1000.");
        }

        return value;
    }

    private static string CopyEntities(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var handles = ReadHandles(arguments);
        var dx = Length(arguments, "dx", units.Scale);
        var dy = Length(arguments, "dy", units.Scale);
        var count = OptionalInteger(arguments, "count", 1, 1, MaxCopies);
        if (Math.Abs(dx) <= Tolerance && Math.Abs(dy) <= Tolerance)
        {
            throw new ArgumentException("dx and dy cannot both be zero; copies would sit on the originals.");
        }
        if (handles.Count * count > MaxHandles)
        {
            throw new ArgumentException($"At most {MaxHandles} copies can be made in one call.");
        }

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        var (found, notFound) = ResolveEntities(document.Database, transaction, handles);
        var modelSpace = OpenModelSpace(document.Database, transaction);
        var created = new List<string>();
        for (var step = 1; step <= count; step++)
        {
            var displacement = Matrix3d.Displacement(new Vector3d(dx * step, dy * step, 0));
            foreach (var entity in found)
            {
                created.Add(AppendTransformedCopy(modelSpace, transaction, entity, displacement));
            }
        }
        transaction.Commit();

        document.Editor.WriteMessage($"\nClaude copied {found.Count} entities {count} times. Use UNDO to remove the copies.");
        return Success($"Created {created.Count} copies.", new { handles = created, notFound });
    }

    private static string RotateEntities(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var handles = ReadHandles(arguments);
        var basePoint = Point(arguments, "baseX", "baseY", units.Scale);
        var angle = DegreesToRadians(OptionalAngle(arguments, "angle", double.NaN));
        if (double.IsNaN(angle))
        {
            throw new ArgumentException("angle is required.");
        }

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        var (found, notFound) = ResolveEntities(document.Database, transaction, handles);
        var rotation = Matrix3d.Rotation(angle, Vector3d.ZAxis, basePoint);
        foreach (var entity in found)
        {
            entity.UpgradeOpen();
            entity.TransformBy(rotation);
        }
        transaction.Commit();

        document.Editor.WriteMessage($"\nClaude rotated {found.Count} entities. Use UNDO to revert.");
        return Success($"Rotated {found.Count} entities.", new { rotated = found.Count, notFound });
    }

    private static string MirrorEntities(JsonElement arguments)
    {
        var document = RequireDocument();
        var units = Units(arguments, document.Database);
        var handles = ReadHandles(arguments);
        var first = Point(arguments, "x1", "y1", units.Scale);
        var second = Point(arguments, "x2", "y2", units.Scale);
        RequireDistinct(first, second, "The mirror line needs two different points.");
        var keepOriginal = OptionalBoolean(arguments, "keepOriginal", true);

        using var documentLock = document.LockDocument();
        using var transaction = document.Database.TransactionManager.StartTransaction();
        var (found, notFound) = ResolveEntities(document.Database, transaction, handles);
        var mirroring = Matrix3d.Mirroring(new Line3d(first, second));
        var modelSpace = OpenModelSpace(document.Database, transaction);
        var created = new List<string>();
        foreach (var entity in found)
        {
            if (keepOriginal)
            {
                created.Add(AppendTransformedCopy(modelSpace, transaction, entity, mirroring));
            }
            else
            {
                entity.UpgradeOpen();
                entity.TransformBy(mirroring);
            }
        }
        transaction.Commit();

        document.Editor.WriteMessage($"\nClaude mirrored {found.Count} entities. Use UNDO to revert.");
        return Success(
            keepOriginal ? $"Created {created.Count} mirrored copies." : $"Mirrored {found.Count} entities.",
            new { handles = created, mirrored = found.Count, notFound }
        );
    }

    private static string AppendTransformedCopy(
        BlockTableRecord modelSpace,
        Transaction transaction,
        Entity entity,
        Matrix3d transform
    )
    {
        using var copy = (Entity)entity.Clone();
        copy.TransformBy(transform);
        modelSpace.AppendEntity(copy);
        transaction.AddNewlyCreatedDBObject(copy, true);
        return copy.Handle.ToString();
    }

    private static Point3d ToPoint(Vec point) => new(point.X, point.Y, 0);
}

internal sealed record FurnitureSymbol(double Width, double Depth, bool Fixture, Func<double, List<Entity>> Build);

// Simple plan symbols in metres: width along X, depth along Y, centred on the
// origin, front edge at -Y (the side a person approaches from).
internal static class FurnitureCatalog
{
    private static readonly Dictionary<string, FurnitureSymbol> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bed"] = new(1.6, 2.0, false, m => new()
        {
            Rect(m, -0.8, -1.0, 0.8, 1.0), Rect(m, -0.8, 0.92, 0.8, 1.0),
            Rect(m, -0.72, 0.55, -0.06, 0.85), Rect(m, 0.06, 0.55, 0.72, 0.85),
            Segment(m, -0.8, 0.35, 0.8, 0.35), Segment(m, 0.15, 0.35, 0.8, -0.3)
        }),
        ["single_bed"] = new(0.9, 2.0, false, m => new()
        {
            Rect(m, -0.45, -1.0, 0.45, 1.0), Rect(m, -0.45, 0.92, 0.45, 1.0),
            Rect(m, -0.36, 0.55, 0.36, 0.85), Segment(m, -0.45, 0.35, 0.45, 0.35)
        }),
        ["wardrobe"] = new(1.2, 0.6, false, m => new()
        {
            Rect(m, -0.6, -0.3, 0.6, 0.3), Segment(m, -0.55, 0.0, 0.55, 0.0), Segment(m, 0.0, -0.3, 0.0, 0.3)
        }),
        ["desk"] = new(1.2, 0.6, false, m => new() { Rect(m, -0.6, -0.3, 0.6, 0.3) }),
        ["chair"] = new(0.45, 0.45, false, m => new()
        {
            Rect(m, -0.225, -0.225, 0.225, 0.225), Rect(m, -0.225, 0.17, 0.225, 0.225)
        }),
        ["table"] = new(1.6, 0.9, false, m => new() { Rect(m, -0.8, -0.45, 0.8, 0.45) }),
        ["round_table"] = new(1.0, 1.0, false, m => new() { Round(m, 0, 0, 0.5) }),
        ["sofa"] = new(2.0, 0.9, false, m => new()
        {
            Rect(m, -1.0, -0.45, 1.0, 0.45), Rect(m, -1.0, 0.25, 1.0, 0.45),
            Rect(m, -1.0, -0.45, -0.8, 0.25), Rect(m, 0.8, -0.45, 1.0, 0.25), Segment(m, 0.0, -0.45, 0.0, 0.25)
        }),
        ["armchair"] = new(0.85, 0.85, false, m => new()
        {
            Rect(m, -0.425, -0.425, 0.425, 0.425), Rect(m, -0.425, 0.225, 0.425, 0.425),
            Rect(m, -0.425, -0.425, -0.275, 0.225), Rect(m, 0.275, -0.425, 0.425, 0.225)
        }),
        ["tv_unit"] = new(1.6, 0.45, false, m => new() { Rect(m, -0.8, -0.225, 0.8, 0.225) }),
        ["plant"] = new(0.6, 0.6, false, m => Plant(m)),
        ["wc"] = new(0.4, 0.7, true, m => new()
        {
            Rect(m, -0.2, 0.15, 0.2, 0.35), Oval(m, 0, -0.1, 0.18, 0.24)
        }),
        ["basin"] = new(0.55, 0.45, true, m => new()
        {
            Rect(m, -0.275, -0.225, 0.275, 0.225), Oval(m, 0, -0.02, 0.2, 0.15),
            Round(m, 0, -0.02, 0.02), Round(m, 0, 0.16, 0.02)
        }),
        ["bathtub"] = new(0.75, 1.7, true, m => new()
        {
            Rect(m, -0.375, -0.85, 0.375, 0.85), RoundedRect(m, -0.315, -0.79, 0.315, 0.79, 0.12),
            Round(m, 0, 0.65, 0.03)
        }),
        ["shower"] = new(0.9, 0.9, true, m => new()
        {
            Rect(m, -0.45, -0.45, 0.45, 0.45), Segment(m, -0.45, -0.45, 0.45, 0.45),
            Segment(m, -0.45, 0.45, 0.45, -0.45), Round(m, 0, 0, 0.03)
        }),
        ["kitchen_sink"] = new(0.8, 0.5, true, m => new()
        {
            Rect(m, -0.4, -0.25, 0.4, 0.25), Rect(m, -0.36, -0.2, -0.02, 0.18), Rect(m, 0.02, -0.2, 0.36, 0.18),
            Round(m, -0.19, -0.01, 0.025), Round(m, 0.19, -0.01, 0.025)
        }),
        ["stove"] = new(0.6, 0.6, true, m => new()
        {
            Rect(m, -0.3, -0.3, 0.3, 0.3), Round(m, -0.14, -0.14, 0.08), Round(m, 0.14, -0.14, 0.08),
            Round(m, -0.14, 0.14, 0.08), Round(m, 0.14, 0.14, 0.08)
        }),
        ["fridge"] = new(0.7, 0.7, true, m => new()
        {
            Rect(m, -0.35, -0.35, 0.35, 0.35), Segment(m, -0.35, -0.35, 0.35, 0.35), Segment(m, -0.35, 0.35, 0.35, -0.35)
        }),
        ["washing_machine"] = new(0.6, 0.6, true, m => new()
        {
            Rect(m, -0.3, -0.3, 0.3, 0.3), Round(m, 0, -0.03, 0.22)
        })
    };

    internal static string[] Kinds { get; } = Symbols.Keys.ToArray();

    internal static FurnitureSymbol Get(string kind) => Symbols[kind];

    private static Polyline Rect(double m, double x0, double y0, double x1, double y1)
    {
        var polyline = new Polyline();
        polyline.AddVertexAt(0, new Point2d(x0 * m, y0 * m), 0, 0, 0);
        polyline.AddVertexAt(1, new Point2d(x1 * m, y0 * m), 0, 0, 0);
        polyline.AddVertexAt(2, new Point2d(x1 * m, y1 * m), 0, 0, 0);
        polyline.AddVertexAt(3, new Point2d(x0 * m, y1 * m), 0, 0, 0);
        polyline.Closed = true;
        return polyline;
    }

    // Rectangle with quarter-circle corners of radius r.
    private static Polyline RoundedRect(double m, double x0, double y0, double x1, double y1, double r)
    {
        var bulge = Math.Tan(Math.PI / 8);
        var points = new (double X, double Y, double Bulge)[]
        {
            (x0 + r, y0, 0), (x1 - r, y0, bulge), (x1, y0 + r, 0), (x1, y1 - r, bulge),
            (x1 - r, y1, 0), (x0 + r, y1, bulge), (x0, y1 - r, 0), (x0, y0 + r, bulge)
        };
        var polyline = new Polyline();
        for (var i = 0; i < points.Length; i++)
        {
            polyline.AddVertexAt(i, new Point2d(points[i].X * m, points[i].Y * m), points[i].Bulge, 0, 0);
        }
        polyline.Closed = true;
        return polyline;
    }

    private static Line Segment(double m, double x0, double y0, double x1, double y1) =>
        new(new Point3d(x0 * m, y0 * m, 0), new Point3d(x1 * m, y1 * m, 0));

    private static Circle Round(double m, double x, double y, double r) =>
        new(new Point3d(x * m, y * m, 0), Vector3d.ZAxis, r * m);

    // Ellipse with half-width rx and half-depth ry; assumes rx <= ry.
    private static Entity Oval(double m, double x, double y, double rx, double ry) =>
        new Ellipse(new Point3d(x * m, y * m, 0), Vector3d.ZAxis, new Vector3d(0, ry * m, 0), rx / ry, 0, 2 * Math.PI);

    private static List<Entity> Plant(double m)
    {
        var shapes = new List<Entity> { Round(m, 0, 0, 0.3) };
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4;
            shapes.Add(Segment(m, 0.05 * Math.Cos(angle), 0.05 * Math.Sin(angle), 0.3 * Math.Cos(angle), 0.3 * Math.Sin(angle)));
        }

        return shapes;
    }
}
