using System;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADClaudeBridge;

// Shared drafting for the ceiling feature tools (draw_band_part,
// draw_bent_bar_part, add_face_graphic): sheet text, dimensions sized to the
// sheet text height and reading in the caller's units, view markers, and
// outlines and solid fills built from BandLoop geometry.
internal static partial class BridgeHost
{
    private const string BandLayerDefault = "SIGN-BAND";
    private const string LightLayerDefault = "SIGN-LIGHT";
    private const string GraphicLayerDefault = "SIGN-GRAPHIC";
    private const string SignDimensionLayerDefault = "SIGN-DIM";
    private const string SignAnnotationLayerDefault = "SIGN-ANNO";

    // Everything one tool call draws goes through one writer inside one
    // transaction, so a single UNDO removes it.
    private sealed class SheetWriter
    {
        private readonly Database database;
        private readonly Transaction transaction;
        private readonly BlockTableRecord modelSpace;
        private readonly double unitScale;
        private readonly string dimensionLayer;
        private readonly string annotationLayer;

        internal SheetWriter(
            Database database,
            Transaction transaction,
            double textHeight,
            double unitScale,
            string dimensionLayer,
            string annotationLayer
        )
        {
            this.database = database;
            this.transaction = transaction;
            modelSpace = OpenModelSpace(database, transaction);
            TextHeight = textHeight;
            this.unitScale = unitScale;
            this.dimensionLayer = dimensionLayer;
            this.annotationLayer = annotationLayer;
        }

        // Sheet text height in drawing units; layout spacing is a multiple of it.
        internal double TextHeight { get; }

        // A drawing length in the caller's units with one decimal.
        internal string Format(double drawingValue) =>
            (drawingValue / unitScale).ToString("0.0", CultureInfo.InvariantCulture);

        internal string Add(Entity entity, string layerName, Color? color = null)
        {
            entity.Layer = layerName;
            if (color is not null)
            {
                entity.Color = color;
            }
            modelSpace.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            return entity.Handle.ToString();
        }

        internal string Note(Vec location, double height, string contents, AttachmentPoint attachment) =>
            Text(location, height, contents, attachment, annotationLayer, null).Handle;

        // Text on any layer, returning where it actually landed.
        internal (string Handle, Vec Min, Vec Max) Text(
            Vec location,
            double height,
            string contents,
            AttachmentPoint attachment,
            string layerName,
            Color? color
        )
        {
            using var mtext = new MText();
            mtext.SetDatabaseDefaults();
            mtext.Location = ToPoint(location);
            mtext.TextHeight = height;
            mtext.Attachment = attachment;
            mtext.Contents = MTextContents(contents);
            var handle = Add(mtext, layerName, color);
            var extents = mtext.GeometricExtents;
            return (handle, new Vec(extents.MinPoint.X, extents.MinPoint.Y), new Vec(extents.MaxPoint.X, extents.MaxPoint.Y));
        }

        internal string Aligned(Vec first, Vec second, Vec dimensionLine)
        {
            using var dimension = new AlignedDimension(ToPoint(first), ToPoint(second), ToPoint(dimensionLine), string.Empty, database.Dimstyle);
            return Measure(dimension);
        }

        // angle 0 measures along X, pi / 2 along Y.
        internal string Rotated(double angle, Vec first, Vec second, Vec dimensionLine)
        {
            using var dimension = new RotatedDimension(angle, ToPoint(first), ToPoint(second), ToPoint(dimensionLine), string.Empty, database.Dimstyle);
            return Measure(dimension);
        }

        // A filled triangle whose tip touches `tip`, pointing along the unit
        // vector `pointing`: the view direction marker beside a face.
        internal string Marker(Vec tip, Vec pointing)
        {
            var side = new Vec(-pointing.Y, pointing.X) * (TextHeight * 0.6);
            var baseCentre = tip - pointing * (TextHeight * 1.2);
            using var solid = new Solid(ToPoint(tip), ToPoint(baseCentre + side), ToPoint(baseCentre - side), ToPoint(baseCentre - side));
            return Add(solid, annotationLayer);
        }

        internal string Outline(BandLoop loop, string layerName, Color? color = null)
        {
            using var polyline = LoopPolyline(loop);
            return Add(polyline, layerName, color);
        }

        internal string Segment(Vec start, Vec end, string layerName, Color? color = null)
        {
            using var line = new Line(ToPoint(start), ToPoint(end));
            return Add(line, layerName, color);
        }

        // A non-associative solid fill of `outer`, leaving `hole` empty.
        internal string Fill(BandLoop outer, BandLoop? hole, string layerName, Color color)
        {
            using var hatch = new Hatch();
            hatch.SetDatabaseDefaults();
            Add(hatch, layerName, color);
            hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            hatch.Associative = false;
            hatch.AppendLoop(HatchLoopTypes.External | HatchLoopTypes.Polyline, LoopPoints(outer), LoopBulges(outer));
            if (hole is not null)
            {
                hatch.AppendLoop(HatchLoopTypes.Polyline, LoopPoints(hole), LoopBulges(hole));
            }
            hatch.EvaluateHatch(true);
            return hatch.Handle.ToString();
        }

        // Sizes the dimension to the sheet text height and makes it read in
        // the caller's units with one decimal, e.g. 130.0 in a millimetre
        // drawing dimensioned in cm. DIMSCALE is reset so the drawing's
        // dimension style cannot scale the sizes a second time.
        private string Measure(Dimension dimension)
        {
            dimension.SetDatabaseDefaults();
            var handle = Add(dimension, dimensionLayer);
            dimension.Dimscale = 1;
            dimension.Dimtxt = TextHeight * 0.8;
            dimension.Dimasz = TextHeight * 0.6;
            dimension.Dimexe = TextHeight * 0.4;
            dimension.Dimexo = TextHeight * 0.3;
            dimension.Dimgap = TextHeight * 0.25;
            dimension.Dimdec = 1;
            dimension.Dimzin = 0;
            dimension.Dimlfac = 1 / unitScale;
            dimension.Dimtad = 1;
            dimension.Dimtih = false;
            dimension.Dimtoh = false;
            dimension.RecomputeDimensionBlock(true);
            return handle;
        }
    }

    private static Color? OptionalColorIndex(JsonElement arguments, string name)
    {
        if (!HasValue(arguments, name))
        {
            return null;
        }
        var property = arguments.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var index) || index is < 1 or > 255)
        {
            throw new ArgumentException($"{name} must be an AutoCAD Color Index from 1 to 255.");
        }
        return Color.FromColorIndex(ColorMethod.ByAci, (short)index);
    }

    // Reads a nested object argument, prefixing its errors with its name.
    private static T Nested<T>(JsonElement arguments, string name, Func<JsonElement, T> read)
    {
        if (!HasValue(arguments, name))
        {
            throw new ArgumentException($"{name} is required.");
        }
        var element = arguments.GetProperty(name);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"{name} must be an object.");
        }
        try
        {
            return read(element);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"{name}.{exception.Message}");
        }
    }

    private static Polyline LoopPolyline(BandLoop loop)
    {
        var polyline = new Polyline();
        for (var index = 0; index < loop.Vertices.Count; index++)
        {
            polyline.AddVertexAt(index, new Point2d(loop.Vertices[index].X, loop.Vertices[index].Y), loop.Bulges[index], 0, 0);
        }
        polyline.Closed = true;
        return polyline;
    }

    private static Point2dCollection LoopPoints(BandLoop loop)
    {
        var points = new Point2dCollection();
        foreach (var vertex in loop.Vertices)
        {
            points.Add(new Point2d(vertex.X, vertex.Y));
        }
        points.Add(new Point2d(loop.Vertices[0].X, loop.Vertices[0].Y));
        return points;
    }

    private static DoubleCollection LoopBulges(BandLoop loop)
    {
        var bulges = new DoubleCollection();
        foreach (var bulge in loop.Bulges)
        {
            bulges.Add(bulge);
        }
        bulges.Add(0);
        return bulges;
    }

    private static BandLoop Rectangle(Vec min, Vec max) =>
        new(
            new() { min, new Vec(max.X, min.Y), max, new Vec(min.X, max.Y) },
            new() { 0, 0, 0, 0 },
            2 * (max.X - min.X + max.Y - min.Y)
        );

    private static BandLoop Translate(BandLoop loop, Vec offset) =>
        new(loop.Vertices.ConvertAll(vertex => vertex + offset), loop.Bulges, loop.Length);
}
