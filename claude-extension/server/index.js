"use strict";

const fs = require("node:fs");
const net = require("node:net");
const path = require("node:path");
const readline = require("node:readline");

const HOST = "127.0.0.1";
const PORT = 51327;

function loadToken() {
  const localAppData = process.env.LOCALAPPDATA;
  if (!localAppData) {
    throw new Error("LOCALAPPDATA is unavailable; the AutoCAD bridge token cannot be located.");
  }

  const tokenFile = path.join(localAppData, "AutoCADClaudeBridge", "bridge-token.txt");
  let token;
  try {
    token = fs.readFileSync(tokenFile, "utf8").trim();
  } catch {
    throw new Error(
      "The AutoCAD bridge token is missing. Reinstall the bridge or start AutoCAD once, then try again."
    );
  }

  if (!/^[0-9a-fA-F]{64}$/.test(token)) {
    throw new Error("The AutoCAD bridge token is invalid. Reinstall the bridge.");
  }
  return token;
}

const MAX_COORDINATE = 1e9;
const MAX_SIZE = 1e6;
const MAX_POINTS = 10000;
const MAX_TEXT = 2000;
const MAX_HANDLES = 1000;

const coordinate = (description) => ({
  type: "number",
  minimum: -MAX_COORDINATE,
  maximum: MAX_COORDINATE,
  description,
});
const size = (description) => ({
  type: "number",
  exclusiveMinimum: 0,
  maximum: MAX_SIZE,
  description,
});
const angle = (description) => ({ type: "number", minimum: -360, maximum: 360, description });
const point = {
  type: "object",
  properties: { x: coordinate("X coordinate."), y: coordinate("Y coordinate.") },
  required: ["x", "y"],
  additionalProperties: false,
};
const segment = (description) => ({
  type: "object",
  description,
  properties: { start: point, end: point },
  required: ["start", "end"],
  additionalProperties: false,
});
const layer = {
  type: "string",
  default: "AI_DRAWING",
  minLength: 1,
  maxLength: 64,
  pattern: "^[A-Za-z0-9_-]+$",
  description: "Layer name using letters, numbers, underscores, or hyphens.",
};
const color = {
  type: "integer",
  minimum: 1,
  maximum: 255,
  description: "AutoCAD Color Index 1-255. Omit for ByLayer.",
};
const text = (description) => ({ type: "string", maxLength: MAX_TEXT, description });
const handleSchema = {
  type: "string",
  pattern: "^[0-9A-Fa-f]{1,16}$",
  description: "Hexadecimal entity handle returned by a drawing tool or list_entities.",
};
const handlesSchema = {
  type: "array",
  items: handleSchema,
  minItems: 1,
  maxItems: MAX_HANDLES,
  description: "Handles of the exact entities to act on.",
};
const drawingHints = { readOnlyHint: false, destructiveHint: false, idempotentHint: false };

// Lengths go to AutoCAD exactly as the user stated them, tagged with their
// unit; the plug-in converts them to the drawing's INSUNITS.
const units = {
  type: "string",
  enum: ["mm", "cm", "dm", "m", "km", "in", "ft", "yd", "mi"],
  description:
    "Unit of every length in this call (coordinates, sizes, offsets, text heights). Set it to the unit the user stated and pass their numbers unchanged; AutoCAD converts them to drawing units. Omit only when the values are already in drawing units.",
};
const roomLayers = (description) => ({
  type: "array",
  items: { ...layer, default: undefined },
  minItems: 1,
  maxItems: 20,
  description,
});
const roomDetectionOptions = {
  maxOpeningWidth: size("Widest doorway or opening to close along a wall line."),
  maxDraftingGap: size("Widest gap to close in any direction where wall lines nearly meet."),
  minRoomWidth: size("Narrower areas, such as the space between the two lines of a wall, are not rooms."),
  minRoomArea: size("Smallest room area, in the square of `units`."),
};
const roomIdSchema = {
  type: "string",
  pattern: "^R[0-9]{1,7}$",
  description: "Saved room ID such as R1, from save_rooms or get_rooms.",
};
const UNIT_RULE =
  " Pass the user's numbers exactly as stated with `units` set to the unit they used (e.g. 300 cm -> 300 with units \"cm\"). Never convert units yourself. If the user gave no unit, ask them.";

const tools = [
  {
    name: "autocad_status",
    description:
      "Check whether AutoCAD 2027 is connected and return the active drawing name and its units (units, insunits, lunits, measurement, dimlfac, dimscale). Use this before any drawing action. If units is \"unitless\", ask the user what one drawing unit means before drawing with a unit.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
  },
  {
    name: "draw_line",
    description:
      "Draw exactly one straight line in the active AutoCAD drawing." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        startX: { type: "number", description: "Starting X coordinate." },
        startY: { type: "number", description: "Starting Y coordinate." },
        endX: { type: "number", description: "Ending X coordinate." },
        endY: { type: "number", description: "Ending Y coordinate." },
        units,
        layer: {
          type: "string",
          default: "AI_DRAWING",
          description: "Layer name using letters, numbers, underscores, or hyphens.",
        },
      },
      required: ["startX", "startY", "endX", "endY"],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false },
  },
  {
    name: "draw_rectangle",
    description:
      "Draw exactly one axis-aligned closed rectangle in the active AutoCAD drawing." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        x: { type: "number", description: "Lower-left X coordinate." },
        y: { type: "number", description: "Lower-left Y coordinate." },
        width: { type: "number", exclusiveMinimum: 0, description: "Rectangle width." },
        height: { type: "number", exclusiveMinimum: 0, description: "Rectangle height." },
        units,
        layer: {
          type: "string",
          default: "AI_DRAWING",
          description: "Layer name using letters, numbers, underscores, or hyphens.",
        },
      },
      required: ["x", "y", "width", "height"],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false },
  },
  {
    name: "draw_polyline",
    description:
      "Draw one 2D polyline through the given points in the active drawing. Set closed to true to join the last point back to the first (needs at least 3 points)." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        points: {
          type: "array",
          items: point,
          minItems: 2,
          maxItems: MAX_POINTS,
          description: "Vertices in drawing units.",
        },
        closed: { type: "boolean", default: false, description: "Close the polyline." },
        color,
        layer,
      },
      required: ["points"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "add_linear_dimension",
    description:
      "Add one linear (rotated) dimension measuring the distance between two points, using the current DIMSTYLE. rotation 0 measures horizontally, 90 vertically." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        x1: coordinate("First measured point X."),
        y1: coordinate("First measured point Y."),
        x2: coordinate("Second measured point X."),
        y2: coordinate("Second measured point Y."),
        dimLineX: coordinate("X of a point the dimension line passes through."),
        dimLineY: coordinate("Y of a point the dimension line passes through."),
        rotation: angle("Dimension line angle in degrees (0 = horizontal, 90 = vertical)."),
        textOverride: text("Optional text replacing the measured value. Use <> to include the measurement."),
        textHeight: size("Optional dimension text height."),
        color,
        layer,
      },
      required: ["x1", "y1", "x2", "y2", "dimLineX", "dimLineY"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "add_aligned_dimension",
    description:
      "Add one aligned dimension parallel to the line between two points, using the current DIMSTYLE. offset is the signed distance of the dimension line from that line (positive = left side when walking from point 1 to point 2)." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        x1: coordinate("First measured point X."),
        y1: coordinate("First measured point Y."),
        x2: coordinate("Second measured point X."),
        y2: coordinate("Second measured point Y."),
        offset: {
          type: "number",
          minimum: -MAX_SIZE,
          maximum: MAX_SIZE,
          description: "Signed, non-zero offset of the dimension line.",
        },
        textOverride: text("Optional text replacing the measured value. Use <> to include the measurement."),
        textHeight: size("Optional dimension text height."),
        color,
        layer,
      },
      required: ["x1", "y1", "x2", "y2", "offset"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "add_angular_dimension",
    description:
      "Add one angular dimension between two non-parallel lines, using the current DIMSTYLE. The arc point picks which of the angles is dimensioned." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        line1: segment("First line, as start and end points."),
        line2: segment("Second line, as start and end points."),
        arcX: coordinate("X of a point the dimension arc passes through."),
        arcY: coordinate("Y of a point the dimension arc passes through."),
        textHeight: size("Optional dimension text height."),
        color,
        layer,
      },
      required: ["line1", "line2", "arcX", "arcY"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "add_hatch",
    description:
      "Fill one closed polygon with a non-associative hatch. pattern defaults to SOLID; any predefined AutoCAD pattern name such as ANSI31 can be used." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        boundary: {
          type: "array",
          items: point,
          minItems: 3,
          maxItems: MAX_POINTS,
          description: "Polygon vertices in order; the polygon is closed automatically.",
        },
        pattern: {
          type: "string",
          default: "SOLID",
          minLength: 1,
          maxLength: 64,
          pattern: "^[A-Za-z0-9_-]+$",
          description: "Predefined hatch pattern name.",
        },
        patternScale: size(
          "Pattern scale ratio (ignored for SOLID; not a length, never unit-converted). Omit to scale the pattern to the drawing's units automatically."
        ),
        patternAngle: angle("Pattern angle in degrees (ignored for SOLID)."),
        color,
        layer,
      },
      required: ["boundary"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "add_text",
    description: "Add one multiline text (MText) object to the active drawing." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        x: coordinate("Insertion point X."),
        y: coordinate("Insertion point Y."),
        text: { type: "string", minLength: 1, maxLength: MAX_TEXT, description: "Plain text. Newlines start new lines." },
        height: size("Text height."),
        rotation: angle("Rotation in degrees."),
        attachment: {
          type: "string",
          enum: [
            "TopLeft",
            "TopCenter",
            "TopRight",
            "MiddleLeft",
            "MiddleCenter",
            "MiddleRight",
            "BottomLeft",
            "BottomCenter",
            "BottomRight",
          ],
          default: "TopLeft",
          description: "Which part of the text box sits on the insertion point.",
        },
        color,
        layer,
      },
      required: ["x", "y", "text", "height"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "add_leader",
    description:
      "Add one multileader: an arrow at the arrow point, a leader line to the landing point, and text at the landing." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        arrowX: coordinate("Arrowhead X."),
        arrowY: coordinate("Arrowhead Y."),
        landingX: coordinate("Landing (text end) X."),
        landingY: coordinate("Landing (text end) Y."),
        text: { type: "string", minLength: 1, maxLength: MAX_TEXT, description: "Leader text." },
        textHeight: size("Text height."),
        color,
        layer,
      },
      required: ["arrowX", "arrowY", "landingX", "landingY", "text", "textHeight"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "list_entities",
    description:
      "List model-space entities with handle, type, layer, bounding box and key geometry (line endpoints, polyline vertices, dimension measurement and override text, text content, block name, position, rotation and attributes). Read-only. Set `units` to get lengths back in that unit (e.g. the unit the user is working in); without it values are in drawing units. The result's `units` field says which.",
    inputSchema: {
      type: "object",
      properties: {
        units,
        layer: { ...layer, default: undefined, description: "Only entities on this layer." },
        type: {
          type: "string",
          minLength: 1,
          maxLength: 64,
          pattern: "^[A-Za-z0-9_-]+$",
          description: "Only this type: a DXF name (LINE, LWPOLYLINE, DIMENSION, HATCH, MTEXT, MULTILEADER) or class name (RotatedDimension).",
        },
        limit: { type: "integer", minimum: 1, maximum: 1000, default: 200, description: "Maximum entities returned." },
      },
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
  },
  {
    name: "erase_entities",
    description:
      "Erase exactly the model-space entities with the given handles. Reports handles that were not found. Never acts on a selection or on anything not listed. Undo with UNDO in AutoCAD.",
    inputSchema: {
      type: "object",
      properties: { handles: handlesSchema },
      required: ["handles"],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true },
  },
  {
    name: "delete_layer",
    description:
      "Erase every entity on one layer (model and paper space) and then purge the layer. Refuses layer 0, Defpoints, the current layer, locked layers, and layers still used by block definitions. Confirm with the user before calling.",
    inputSchema: {
      type: "object",
      properties: { layer: { ...layer, default: undefined, description: "Layer to delete." } },
      required: ["layer"],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false },
  },
  {
    name: "move_entities",
    description: "Move exactly the model-space entities with the given handles by dx, dy. Reports handles that were not found." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        handles: handlesSchema,
        dx: coordinate("Displacement along X."),
        dy: coordinate("Displacement along Y."),
      },
      required: ["handles", "dx", "dy"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "set_dimension_props",
    description: "Change the appearance of one existing dimension. Provide at least one property." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        handle: handleSchema,
        textHeight: size("Text height (DIMTXT)."),
        textGap: { type: "number", minimum: 0, maximum: MAX_SIZE, description: "Gap around text (DIMGAP)." },
        arrowSize: { type: "number", minimum: 0, maximum: MAX_SIZE, description: "Arrowhead size (DIMASZ)." },
        textOverride: text("Text override; empty string restores the measured value."),
        color,
      },
      required: ["handle"],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true },
  },
  {
    name: "zoom_extents",
    description: "Zoom the active model-space view to the drawing extents. Does not change the drawing.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
  },
  {
    name: "draw_circle",
    description: "Draw one circle, e.g. a round table, column or fixture detail." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        centerX: coordinate("Centre X."),
        centerY: coordinate("Centre Y."),
        radius: size("Radius."),
        color,
        layer,
      },
      required: ["centerX", "centerY", "radius"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "draw_arc",
    description:
      "Draw one circular arc running counter-clockwise from startAngle to endAngle (degrees, 0 = +X). For door swings in walls prefer draw_walls openings." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        centerX: coordinate("Centre X."),
        centerY: coordinate("Centre Y."),
        radius: size("Radius."),
        startAngle: angle("Start angle in degrees."),
        endAngle: angle("End angle in degrees."),
        color,
        layer,
      },
      required: ["centerX", "centerY", "radius", "startAngle", "endAngle"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "draw_walls",
    description:
      "Draw double-line walls along a centreline path with one thickness and mitred corners. Use closed: true for the exterior outline. Doors, windows and plain openings are placed by segment index (0 = from points[0] to points[1]) and offset from that segment's start to the opening's near edge along the centreline. Doors and openings break the wall lines and get jamb caps and a centreline room separation line (layer A-ROOM-SEP); doors also get a leaf and 90 degree swing (hinge: start/end of the opening, swing: left/right of the path direction). Windows keep the wall lines and add jamb and glazing lines. End interior walls at the face of the wall they meet (half the other wall's thickness from its centreline). Run detect_rooms with wallLayers [wall layer, A-ROOM-SEP]." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        points: {
          type: "array",
          items: point,
          minItems: 2,
          maxItems: MAX_POINTS,
          description: "Wall centreline points in order.",
        },
        closed: { type: "boolean", default: false, description: "Join the last point back to the first." },
        thickness: size("Wall thickness, e.g. 0.2 m exterior, 0.1 m partition."),
        capEnds: { type: "boolean", default: true, description: "Close the two ends of an open path with a short line." },
        openings: {
          type: "array",
          maxItems: 200,
          items: {
            type: "object",
            properties: {
              segment: { type: "integer", minimum: 0, maximum: MAX_POINTS, description: "Path segment index." },
              offset: { type: "number", minimum: 0, maximum: MAX_SIZE, description: "Distance from the segment start to the opening's near edge." },
              width: size("Opening width."),
              kind: { type: "string", enum: ["door", "window", "opening"], default: "door", description: "What fills the opening." },
              hinge: { type: "string", enum: ["start", "end"], default: "start", description: "Door hinge at the near (start) or far (end) edge." },
              swing: { type: "string", enum: ["left", "right"], default: "left", description: "Side of the path the door opens towards." },
            },
            required: ["segment", "offset", "width"],
            additionalProperties: false,
          },
          description: "Doors, windows and openings in these walls.",
        },
        wallLayer: { ...layer, default: "A-WALL" },
        doorLayer: { ...layer, default: "A-DOOR" },
        windowLayer: { ...layer, default: "A-GLAZ" },
        separationLayer: { ...layer, default: "A-ROOM-SEP" },
      },
      required: ["points", "thickness"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "place_furniture",
    description:
      "Place one furniture or fixture symbol as a block (CB_<KIND>) centred at x, y. Width runs along the symbol's X, depth along Y, and the back edge (bed headboard, sofa back, WC tank) faces +Y at rotation 0. To put the back against a wall: wall above = 0, wall on the left = 90, wall below = 180, wall on the right = -90; place the centre half the depth away from the wall face. Fixtures go on A-FIXT, furniture on A-FURN unless layer is given. Omit width/depth for standard sizes." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        kind: {
          type: "string",
          enum: [
            "bed", "single_bed", "wardrobe", "desk", "chair", "table", "round_table", "sofa", "armchair",
            "tv_unit", "plant", "wc", "basin", "bathtub", "shower", "kitchen_sink", "stove", "fridge", "washing_machine",
          ],
          description: "Symbol to place.",
        },
        x: coordinate("Centre X."),
        y: coordinate("Centre Y."),
        rotation: angle("Rotation in degrees, counter-clockwise."),
        width: size("Optional width; default is the standard size."),
        depth: size("Optional depth; default is the standard size."),
        layer: { ...layer, default: undefined, description: "Optional layer; default A-FURN or A-FIXT." },
      },
      required: ["kind", "x", "y"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "draw_band_part",
    description:
      "Draw one ceiling band part as a production drawing: a closed ring of sheet material standing on edge (chandelier ring, frame, lit band). Draws the plan (outer and inner outline, face letters A, B, C... from points[0]->points[1] onward, edge dimensions, thickness, radius notes, title with quantity, developed-length summary) and to its right one elevation per face, with faces of equal length sharing one elevation (\"B,D\"). Elevation length = the face's corner-to-corner length, so plan and elevations always agree; the flat length between fillets is noted when it differs. points are the OUTER sharp corners (where the faces would meet without fillets), convex outlines only; cornerRadius is the outer fillet radius and the inner radius is cornerRadius - thickness. lightStrip adds an LED strip band to every elevation. Returns face lengths, perimeters (outer, inner, centreline developed length) and the drawing extents, so the next part can be placed clear of this one." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        name: { ...text("Part name shown as the sheet title, e.g. \"Decorative chandelier\"."), minLength: 1, maxLength: 80 },
        quantity: { type: "integer", minimum: 1, maximum: 10000, default: 1, description: "How many of this part to make." },
        points: {
          type: "array",
          items: point,
          minItems: 3,
          maxItems: 26,
          description: "Outer sharp corners in order around the ring; face A runs from points[0] to points[1].",
        },
        cornerRadius: { type: "number", minimum: 0, maximum: MAX_SIZE, description: "Outer fillet radius at every corner; 0 = sharp." },
        cornerRadii: {
          type: "array",
          items: { type: "number", minimum: 0, maximum: MAX_SIZE },
          minItems: 3,
          maxItems: 26,
          description: "Outer fillet radius per corner, in points order. Overrides cornerRadius.",
        },
        thickness: size("Band material thickness, measured inward from the outer outline."),
        height: size("Band height (the vertical size seen in elevation)."),
        lightStrip: {
          type: "object",
          description: "LED light strip running along every face.",
          properties: {
            height: size("Strip height."),
            offset: { type: "number", minimum: 0, maximum: MAX_SIZE, description: "Distance from the band's bottom edge to the strip. Omit to centre it." },
          },
          required: ["height"],
          additionalProperties: false,
        },
        groupFaces: { type: "boolean", default: true, description: "Draw faces of equal length as one elevation." },
        fillColor: { ...color, description: "AutoCAD Color Index for the band's solid fill in plan and elevation (e.g. 5 blue, 2 yellow, 41 cream). Omit for outline only." },
        lightColor: { ...color, description: "AutoCAD Color Index for the light strip fill. Default 2 (yellow)." },
        textHeight: size("Sheet text height. Default: 1/40 of the part's largest size."),
        bandLayer: { ...layer, default: "SIGN-BAND" },
        lightLayer: { ...layer, default: "SIGN-LIGHT" },
        dimensionLayer: { ...layer, default: "SIGN-DIM" },
        annotationLayer: { ...layer, default: "SIGN-ANNO" },
      },
      required: ["name", "points", "thickness", "height"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "draw_bent_bar_part",
    description:
      "Draw one bent bar part as a production drawing: a strip of sheet standing on edge that follows straight runs and arcs in plan (decorative arrow, swoosh). The path is the strip's centreline, starting at start and heading startAngle; each arc turns left or right by angle (or by its centreline length) at radius, tangent to the run before it. Draws the plan (outline, run dimensions on the outside of the turn, bend notes, START/END, overall width and depth, title, summary) and to its right the developed elevation: the strip unrolled to its full cut length, bend lines with numbers, chained run lengths, height, and arrowheads. An arrowhead is two 45 degree arms as wide as the strip is tall whose outer edges span arrowHeight. Returns the developed length, every piece with its start distance along the strip, and the drawing extents." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        name: { ...text("Part name shown as the sheet title, e.g. \"Decorative arrow\"."), minLength: 1, maxLength: 80 },
        quantity: { type: "integer", minimum: 1, maximum: 10000, default: 1, description: "How many of this part to make." },
        start: { ...point, description: "Start point of the centreline." },
        startAngle: angle("Direction of the first step in degrees, 0 = +X, 90 = +Y. Default 0."),
        segments: {
          type: "array",
          minItems: 1,
          maxItems: 50,
          description: "Path steps in order.",
          items: {
            type: "object",
            properties: {
              kind: { type: "string", enum: ["line", "arc"], description: "Straight run or tangent arc." },
              length: size("Line: run length. Arc: length along the centreline (instead of angle)."),
              radius: size("Arc: centreline radius; must be more than half the thickness."),
              angle: { type: "number", exclusiveMinimum: 0, maximum: 359.9, description: "Arc: how far it turns, in degrees (instead of length)." },
              turn: { type: "string", enum: ["left", "right"], default: "left", description: "Arc: turn direction seen walking along the path." },
            },
            required: ["kind"],
            additionalProperties: false,
          },
        },
        thickness: size("Strip thickness seen in plan."),
        height: size("Strip height seen in elevation."),
        ends: {
          type: "object",
          properties: {
            start: { type: "string", enum: ["plain", "arrow"], default: "plain", description: "Shape of the start end." },
            end: { type: "string", enum: ["plain", "arrow"], default: "plain", description: "Shape of the end end." },
          },
          additionalProperties: false,
          description: "End shapes in the developed elevation.",
        },
        arrowHeight: size("Overall height of an arrowhead, tip arm to tip arm. Required when an end is an arrow; must exceed 2.414 x height."),
        fillColor: { ...color, description: "AutoCAD Color Index for the solid fill in plan and elevation (e.g. 41 cream). Omit for outline only." },
        textHeight: size("Sheet text height. Default: 1/40 of the part's largest size."),
        barLayer: { ...layer, default: "SIGN-BAND" },
        dimensionLayer: { ...layer, default: "SIGN-DIM" },
        annotationLayer: { ...layer, default: "SIGN-ANNO" },
      },
      required: ["name", "start", "segments", "thickness", "height"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "add_face_graphic",
    description:
      "Add cut letters or chevrons to one band face elevation drawn by draw_band_part. Pass elevation exactly as draw_band_part returned it in elevations[] (x, y of the bottom-left corner, length, height) and the same textHeight. Draw the band with groupFaces false when faces sharing a length carry different graphics. letters: text, letterHeight, at (distance from the face's left end to the first letter; omit to centre), finish. chevrons: at (left end to the row's left-most point), count, stroke (width along the face), pitch (start to start, default 2 x stroke), depth (how far the point leads the back, default half the chevron height), chevronHeight (default the band height; taller ones stand proud of the band top and bottom equally), pointing. Refuses graphics that run off the face (or, for letters, off the band height) and draws nothing then. Adds chained position dimensions above the face and a specification note below." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        elevation: {
          type: "object",
          description: "The face elevation from draw_band_part's elevations[].",
          properties: {
            x: coordinate("Left end of the elevation (bottom-left corner X)."),
            y: coordinate("Band bottom edge (bottom-left corner Y)."),
            length: size("Face length."),
            height: size("Band height."),
          },
          required: ["x", "y", "length", "height"],
          additionalProperties: false,
        },
        kind: { type: "string", enum: ["letters", "chevrons"], description: "What to add." },
        text: { ...text("letters: the text, e.g. COFFEE."), minLength: 1, maxLength: 200 },
        letterHeight: size("letters: capital letter height."),
        finish: { type: "string", enum: ["cut_through", "engraved", "applied"], default: "cut_through", description: "letters: how they are made." },
        at: { type: "number", minimum: 0, maximum: MAX_SIZE, description: "Distance from the face's left end to the graphic's left-most point." },
        count: { type: "integer", minimum: 1, maximum: 20, default: 2, description: "chevrons: how many." },
        stroke: size("chevrons: width of each chevron measured along the face."),
        pitch: size("chevrons: distance from one chevron's start to the next."),
        depth: { type: "number", minimum: 0, maximum: MAX_SIZE, description: "chevrons: how far the point leads the back corners." },
        chevronHeight: size("chevrons: overall height, centred on the band."),
        pointing: { type: "string", enum: ["right", "left"], default: "right", description: "chevrons: direction they point." },
        color: { ...color, description: "AutoCAD Color Index for the graphic. Default 7 (white/black)." },
        textHeight: size("Sheet text height; pass draw_band_part's textHeight."),
        graphicLayer: { ...layer, default: "SIGN-GRAPHIC" },
        dimensionLayer: { ...layer, default: "SIGN-DIM" },
        annotationLayer: { ...layer, default: "SIGN-ANNO" },
      },
      required: ["elevation", "kind"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "check_production_book",
    description:
      "Check a ceiling feature part spec (the JSON from the ceiling-feature-production skill) before drawing or issuing it. Rebuilds every band and bent bar with the drawing tools' own geometry and reports findings: error = cannot be drawn as specified (radii that do not fit, graphics off a face or taller than the band, unknown faces or materials, equal faces sharing an elevation with different graphics); warning = must be resolved before issuing (RAL TBD, unconfirmed parts, values still listed in source.estimated, open questions, graphics running onto corner fillets, no assembly or hangers); info. Returns drawable, issuable, the findings and each part's face lengths and developed length. Draws nothing.",
    inputSchema: {
      type: "object",
      properties: {
        spec: { type: "object", description: "The whole part spec: job, materials, parts, assembly, openQuestions." },
      },
      required: ["spec"],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
  },
  {
    name: "draw_assembly_plan",
    description:
      "Draw the production book's assembly plan: every part's plan outline in its hanging position (each part's own geometry shifted by at), callouts naming each part on the right, hanger points H1, H2... and overall width and depth. Bands take points, cornerRadius or cornerRadii and thickness as in draw_band_part; bent bars take start, startAngle, segments and thickness as in draw_bent_bar_part. Returns the extents to pass to draw_sheet." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        title: { ...text("Plan title, e.g. \"CHANDELIER PLAN\". Default PLAN."), maxLength: 80 },
        parts: {
          type: "array",
          minItems: 1,
          maxItems: 30,
          description: "Parts in the assembly.",
          items: {
            type: "object",
            properties: {
              label: { ...text("Callout text, e.g. \"Decorative arrow\"."), minLength: 1, maxLength: 80 },
              type: { type: "string", enum: ["band", "bent_bar"], description: "Part type." },
              at: { ...point, description: "Offset added to the part's own coordinates. Default 0, 0." },
              points: { type: "array", items: point, minItems: 3, maxItems: 26, description: "band: outer sharp corners." },
              cornerRadius: { type: "number", minimum: 0, maximum: MAX_SIZE, description: "band: outer fillet radius." },
              cornerRadii: { type: "array", items: { type: "number", minimum: 0, maximum: MAX_SIZE }, minItems: 3, maxItems: 26, description: "band: radius per corner." },
              start: { ...point, description: "bent_bar: centreline start." },
              startAngle: angle("bent_bar: first direction in degrees."),
              segments: {
                type: "array",
                minItems: 1,
                maxItems: 50,
                description: "bent_bar: path steps as in draw_bent_bar_part.",
                items: {
                  type: "object",
                  properties: {
                    kind: { type: "string", enum: ["line", "arc"] },
                    length: size("Run length, or arc length along the centreline."),
                    radius: size("Arc centreline radius."),
                    angle: { type: "number", exclusiveMinimum: 0, maximum: 359.9 },
                    turn: { type: "string", enum: ["left", "right"] },
                  },
                  required: ["kind"],
                  additionalProperties: false,
                },
              },
              thickness: size("Band or bar thickness in plan."),
              fillColor: { ...color, description: "AutoCAD Color Index fill. Omit for outline only." },
            },
            required: ["label", "type", "thickness"],
            additionalProperties: false,
          },
        },
        hangers: { type: "array", items: point, maxItems: 50, description: "Hanger points in assembly coordinates." },
        textHeight: size("Sheet text height. Default: 1/40 of the assembly's largest size."),
        bandLayer: { ...layer, default: "SIGN-BAND" },
        dimensionLayer: { ...layer, default: "SIGN-DIM" },
        annotationLayer: { ...layer, default: "SIGN-ANNO" },
      },
      required: ["parts"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "draw_sheet",
    description:
      "Draw one production book sheet around drawing already in model space: a landscape A3 (or A4) frame scaled so extents fit, the Trust-Lines title block (dark bar with project number, site and title; date; the three empty sign-off boxes ÜRETİM KOORDİNATÖRÜ, ÜRETİM SORUMLUSU, PROJE YÖNETİCİSİ; sheet number, sheet name and units; the TRUSTLINES_LOGO block from the drawing or block library, or a text placeholder), a material legend with colour swatches and the hanger symbol, and DRAFT — NOT FOR PRODUCTION when draft is true. Pass the extents a part or assembly tool returned. The sheet is saved in the drawing so plot_sheets_pdf can plot the book in sheet-number order. Always pass draft: true while check_production_book reports the spec is not issuable." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        extents: {
          type: "object",
          description: "Area the sheet must hold, e.g. a part tool's extents.",
          properties: { min: point, max: point },
          required: ["min", "max"],
          additionalProperties: false,
        },
        job: {
          type: "object",
          properties: {
            number: { ...text("Project number, e.g. 435."), maxLength: 20 },
            site: { ...text("Site address line."), maxLength: 120 },
            title: { ...text("Feature title, e.g. CHANDELIER."), maxLength: 60 },
            date: { ...text("Issue date as it should print, e.g. 07.07.2026."), maxLength: 20 },
          },
          additionalProperties: false,
        },
        sheetName: { ...text("What this sheet shows, e.g. \"Decorative arrow\" or \"Plan\"."), minLength: 1, maxLength: 60 },
        sheetNumber: { type: "integer", minimum: 1, maximum: 999, default: 1, description: "Position in the book." },
        sheetCount: { type: "integer", minimum: 1, maximum: 999, description: "Total sheets in the book." },
        paper: { type: "string", enum: ["A3", "A4"], default: "A3", description: "Landscape paper size." },
        materials: {
          type: "array",
          maxItems: 8,
          description: "Legend rows.",
          items: {
            type: "object",
            properties: {
              name: { ...text("Material or finish name."), maxLength: 60 },
              ral: { ...text("RAL code, or TBD."), maxLength: 20 },
              aci: { ...color, description: "Swatch colour (AutoCAD Color Index)." },
            },
            additionalProperties: false,
          },
        },
        hanger: { type: "boolean", default: true, description: "Show the hanger symbol in the legend." },
        draft: { type: "boolean", default: false, description: "Print DRAFT — NOT FOR PRODUCTION." },
        logoBlock: { ...layer, default: "TRUSTLINES_LOGO", description: "Logo block name, from the drawing or the block library." },
        sheetLayer: { ...layer, default: "SIGN-SHEET" },
      },
      required: ["extents", "job", "sheetName"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "list_sheets",
    description:
      "List the production book sheets saved in the drawing by draw_sheet, in book order, with their frame (drawing units) and whether the frame still exists. Read-only.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
  },
  {
    name: "plot_sheets_pdf",
    description:
      "Plot saved sheets to one multi-page PDF with AutoCAD's DWG To PDF driver, each sheet fitted to full-bleed A3 or A4 landscape, in sheet-number order. Omit sheetIds for every saved sheet. Sheets whose frame was erased are skipped and reported. Omit path to write <drawing>-production-book.pdf next to a saved drawing. Never overwrites unless overwrite is true; ask the user first. Can take a few minutes.",
    inputSchema: {
      type: "object",
      properties: {
        path: {
          type: "string",
          minLength: 5,
          maxLength: 260,
          pattern: "^[A-Za-z]:\\\\.*\\.[Pp][Dd][Ff]$",
          description: "Full Windows path of the PDF, e.g. C:\\Projects\\435-book.pdf.",
        },
        sheetIds: {
          type: "array",
          items: { type: "string", pattern: "^S[0-9]{1,7}$" },
          maxItems: 999,
          description: "Sheets to plot, such as S1, S2. Default all.",
        },
        overwrite: { type: "boolean", description: "Replace the file if it exists. Defaults to false." },
      },
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true },
  },
  {
    name: "insert_block",
    description:
      "Insert one block reference by name: a block already defined in the drawing, or <name>.dwg from the bridge block library folder (%LOCALAPPDATA%\\AutoCADClaudeBridge\\blocks). Scales are ratios and are not unit-converted. attributes sets attribute values by tag." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        name: { ...layer, default: undefined, description: "Block name (letters, numbers, underscores, hyphens)." },
        x: coordinate("Insertion point X."),
        y: coordinate("Insertion point Y."),
        rotation: angle("Rotation in degrees, counter-clockwise."),
        scale: { type: "number", exclusiveMinimum: 0, maximum: 1000, description: "Uniform scale ratio." },
        scaleX: { type: "number", exclusiveMinimum: 0, maximum: 1000, description: "X scale ratio." },
        scaleY: { type: "number", exclusiveMinimum: 0, maximum: 1000, description: "Y scale ratio." },
        layer: { ...layer, default: "A-FURN" },
        attributes: {
          type: "object",
          additionalProperties: true,
          description: "Attribute values by tag, e.g. {\"NAME\": \"Bedroom\"}.",
        },
      },
      required: ["name", "x", "y"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "copy_entities",
    description:
      "Copy the entities with the given handles by dx, dy, count times (copy n is offset n times dx, dy), e.g. a row of chairs. Returns the new handles." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        handles: handlesSchema,
        dx: coordinate("Displacement along X per copy."),
        dy: coordinate("Displacement along Y per copy."),
        count: { type: "integer", minimum: 1, maximum: 100, default: 1, description: "Number of copies." },
      },
      required: ["handles", "dx", "dy"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "rotate_entities",
    description: "Rotate the entities with the given handles about a base point by an angle in degrees (counter-clockwise)." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        handles: handlesSchema,
        baseX: coordinate("Base point X."),
        baseY: coordinate("Base point Y."),
        angle: angle("Rotation in degrees."),
      },
      required: ["handles", "baseX", "baseY", "angle"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "mirror_entities",
    description:
      "Mirror the entities with the given handles about the line through (x1, y1) and (x2, y2). keepOriginal (default true) makes mirrored copies; false flips the originals. Text is mirrored as geometry, so mirror text separately or re-add it." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        handles: handlesSchema,
        x1: coordinate("Mirror line first point X."),
        y1: coordinate("Mirror line first point Y."),
        x2: coordinate("Mirror line second point X."),
        y2: coordinate("Mirror line second point Y."),
        keepOriginal: { type: "boolean", default: true, description: "Keep the originals and add mirrored copies." },
      },
      required: ["handles", "x1", "y1", "x2", "y2"],
      additionalProperties: false,
    },
    annotations: drawingHints,
  },
  {
    name: "floor_plan_summary",
    description:
      "Summarize the floor plan in the active drawing: saved plan type, model-space extents with width and height, and entity counts by layer, by type, and by block name (furniture, fixtures, doors). Read-only. Call this first to understand a floor plan before listing entities. Set `units` to get lengths back in that unit.",
    inputSchema: {
      type: "object",
      properties: { units },
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
  },
  {
    name: "query_region",
    description:
      "List model-space entities inside a rectangular region of the floor plan, tested by bounding box. mode \"crossing\" (default) returns entities whose bounding box overlaps the region; \"inside\" returns only those whose bounding box lies fully within it. Read-only. Same entity format as list_entities." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        minX: coordinate("Region lower-left X."),
        minY: coordinate("Region lower-left Y."),
        maxX: coordinate("Region upper-right X."),
        maxY: coordinate("Region upper-right Y."),
        mode: { type: "string", enum: ["crossing", "inside"], default: "crossing", description: "Overlap or full containment." },
        layer: { ...layer, default: undefined, description: "Only entities on this layer." },
        limit: { type: "integer", minimum: 1, maximum: 1000, default: 200, description: "Maximum entities returned." },
      },
      required: ["minX", "minY", "maxX", "maxY"],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
  },
  {
    name: "set_floor_plan_type",
    description:
      "Save the floor plan type in the active drawing so later sessions know what kind of plan it is. Stored inside the DWG; undo with UNDO in AutoCAD. Ask the user if the type is unclear.",
    inputSchema: {
      type: "object",
      properties: {
        planType: {
          type: "string",
          enum: ["architectural", "furniture", "electrical", "plumbing", "reflected_ceiling", "other"],
          description: "Kind of floor plan.",
        },
      },
      required: ["planType"],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true },
  },
  {
    name: "detect_rooms",
    description:
      "Find the rooms of the floor plan as the closed areas formed by walls on the given layers. Doorway gaps up to maxOpeningWidth are closed along the wall line, tiny gaps up to maxDraftingGap are closed in any direction, and remaining open wall ends are flagged. Each room returns net area (holes such as columns removed), area in square metres, perimeter, outline, the text labels and room-tag attributes inside it (its name when there is exactly one), and the blocks inside it. Read-only. Use floor_plan_summary first to find the wall layers. Defaults are 1.2 m, 2 cm, 0.5 m and 1 m²; unitless drawings need them passed explicitly." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        wallLayers: roomLayers("Layers holding walls and any room separation lines."),
        labelLayers: roomLayers("Only use text and room tags on these layers as room labels. Omit to use every non-wall layer."),
        ...roomDetectionOptions,
      },
      required: ["wallLayers"],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
  },
  {
    name: "save_rooms",
    description:
      "Run detect_rooms with the same arguments and save every room in the drawing (Named Objects Dictionary entry CLAUDE_BRIDGE_ROOMS) with an ID, name, status, outline, net and gross area, perimeter, source wall handles with geometry fingerprints, and timestamps. Rooms matching saved ones (by unique label name, then by outline overlap) keep their IDs; saved rooms no longer found are removed. Rooms with several labels are saved with status needs_review. confirmRoomIds marks rooms confirmed only after the user checked them; it is refused for needs_review rooms or when the plan has open wall ends, and then nothing is saved. Undo with UNDO in AutoCAD." + UNIT_RULE,
    inputSchema: {
      type: "object",
      properties: {
        units,
        wallLayers: roomLayers("Layers holding walls and any room separation lines."),
        labelLayers: roomLayers("Only use text and room tags on these layers as room labels. Omit to use every non-wall layer."),
        ...roomDetectionOptions,
        confirmRoomIds: {
          type: "array",
          items: roomIdSchema,
          maxItems: MAX_HANDLES,
          description: "IDs of rooms the user has checked and confirmed. Only pass IDs the user explicitly confirmed.",
        },
      },
      required: ["wallLayers"],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true },
  },
  {
    name: "get_rooms",
    description:
      "Return the rooms saved in the drawing by save_rooms. Each room is checked against the drawing: if any of its source walls was erased, moved, reshaped or put on another layer, its status is stale with the reasons; run save_rooms again to update it. Statuses: ok, needs_review, confirmed, stale. Only confirmed rooms have been checked by the user. Read-only. Set `units` to get lengths and areas back in that unit; square metres are always included.",
    inputSchema: {
      type: "object",
      properties: {
        units,
        includeVertices: { type: "boolean", description: "Include each room outline. Defaults to true." },
      },
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
  },
  {
    name: "export_room_schedule",
    description:
      "Write the saved rooms as a CSV room schedule: room ID, name, status, net and gross area in m², perimeter in m, and warnings. Rooms are checked for staleness first; needs_review and stale rooms keep that status in the file and are never shown as confirmed. Writes to path, or next to the saved DWG as <drawing>-rooms.csv. Refuses to replace an existing file unless overwrite is true; ask the user first. The file is not undone by UNDO.",
    inputSchema: {
      type: "object",
      properties: {
        path: {
          type: "string",
          minLength: 4,
          maxLength: 260,
          pattern: "^[A-Za-z]:\\\\.*\\.[Cc][Ss][Vv]$",
          description: "Full Windows path of the CSV file to write, e.g. C:\\Projects\\rooms.csv. Omit to write next to the drawing.",
        },
        overwrite: { type: "boolean", description: "Replace the file if it exists. Defaults to false." },
      },
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true },
  },
];

function validate(schema, value, at) {
  const where = at || "arguments";
  switch (schema.type) {
    case "object": {
      if (value === null || typeof value !== "object" || Array.isArray(value)) {
        return `${where} must be an object.`;
      }
      const properties = schema.properties || {};
      for (const name of schema.required || []) {
        if (value[name] === undefined) {
          return `${where}.${name} is required.`;
        }
      }
      for (const [name, child] of Object.entries(value)) {
        if (!properties[name]) {
          if (schema.additionalProperties === false) {
            return `${where}.${name} is not allowed.`;
          }
          continue;
        }
        const problem = validate(properties[name], child, `${where}.${name}`);
        if (problem) {
          return problem;
        }
      }
      return null;
    }
    case "array": {
      if (!Array.isArray(value)) {
        return `${where} must be an array.`;
      }
      if (schema.minItems !== undefined && value.length < schema.minItems) {
        return `${where} must contain at least ${schema.minItems} items.`;
      }
      if (schema.maxItems !== undefined && value.length > schema.maxItems) {
        return `${where} must contain at most ${schema.maxItems} items.`;
      }
      for (let index = 0; index < value.length; index++) {
        const problem = validate(schema.items, value[index], `${where}[${index}]`);
        if (problem) {
          return problem;
        }
      }
      return null;
    }
    case "number":
    case "integer": {
      if (typeof value !== "number" || !Number.isFinite(value)) {
        return `${where} must be a finite number.`;
      }
      if (schema.type === "integer" && !Number.isInteger(value)) {
        return `${where} must be an integer.`;
      }
      if (schema.minimum !== undefined && value < schema.minimum) {
        return `${where} must be at least ${schema.minimum}.`;
      }
      if (schema.maximum !== undefined && value > schema.maximum) {
        return `${where} must be at most ${schema.maximum}.`;
      }
      if (schema.exclusiveMinimum !== undefined && value <= schema.exclusiveMinimum) {
        return `${where} must be greater than ${schema.exclusiveMinimum}.`;
      }
      return null;
    }
    case "string": {
      if (typeof value !== "string") {
        return `${where} must be a string.`;
      }
      if (schema.minLength !== undefined && value.length < schema.minLength) {
        return `${where} must contain at least ${schema.minLength} characters.`;
      }
      if (schema.maxLength !== undefined && value.length > schema.maxLength) {
        return `${where} must contain at most ${schema.maxLength} characters.`;
      }
      if (schema.pattern && !new RegExp(schema.pattern).test(value)) {
        return `${where} has an invalid format.`;
      }
      if (schema.enum && !schema.enum.includes(value)) {
        return `${where} must be one of: ${schema.enum.join(", ")}.`;
      }
      return null;
    }
    case "boolean":
      return typeof value === "boolean" ? null : `${where} must be true or false.`;
    default:
      return `${where} has an unsupported schema.`;
  }
}

function writeMessage(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

// Plotting a production book can take minutes; the plug-in allows it 300 s.
const SLOW_TOOLS = new Set(["plot_sheets_pdf"]);

function callAutoCAD(tool, args) {
  return new Promise((resolve, reject) => {
    const token = loadToken();
    const socket = net.createConnection({ host: HOST, port: PORT });
    let response = "";
    const seconds = SLOW_TOOLS.has(tool) ? 310 : 30;
    const timeout = setTimeout(() => {
      socket.destroy();
      reject(new Error(`AutoCAD did not respond within ${seconds} seconds.`));
    }, seconds * 1000);

    socket.setEncoding("utf8");
    socket.on("connect", () => {
      socket.write(`${JSON.stringify({ token, tool, arguments: args || {} })}\n`);
    });
    socket.on("data", (chunk) => {
      response += chunk;
      const newline = response.indexOf("\n");
      if (newline >= 0) {
        clearTimeout(timeout);
        socket.end();
        try {
          resolve(JSON.parse(response.slice(0, newline)));
        } catch {
          reject(new Error("AutoCAD returned an invalid response."));
        }
      }
    });
    socket.on("error", (error) => {
      clearTimeout(timeout);
      if (error.code === "ECONNREFUSED") {
        reject(
          new Error(
            "AutoCAD bridge is unavailable. Open AutoCAD, NETLOAD AutoCADClaudeBridge.dll, and keep a drawing open."
          )
        );
      } else {
        reject(error);
      }
    });
  });
}

async function handle(message) {
  if (message.method === "initialize") {
    return {
      protocolVersion: message.params?.protocolVersion || "2025-06-18",
      capabilities: { tools: {} },
      serverInfo: { name: "autocad-local-bridge", version: "0.2.0" },
    };
  }
  if (message.method === "ping") {
    return {};
  }
  if (message.method === "tools/list") {
    return { tools };
  }
  if (message.method === "tools/call") {
    const name = message.params?.name;
    const args = message.params?.arguments || {};
    const tool = tools.find((candidate) => candidate.name === name);
    if (!tool) {
      throw new Error("Unknown or disabled AutoCAD tool.");
    }
    const problem = validate(tool.inputSchema, args);
    if (problem) {
      return {
        content: [{ type: "text", text: JSON.stringify({ ok: false, message: problem }) }],
        isError: true,
      };
    }
    const result = await callAutoCAD(name, args);
    return {
      content: [{ type: "text", text: JSON.stringify(result) }],
      isError: result.ok !== true,
    };
  }
  throw new Error(`Unsupported MCP method: ${message.method}`);
}

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", async (line) => {
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    return;
  }

  if (message.id === undefined) {
    return;
  }

  try {
    const result = await handle(message);
    writeMessage({ jsonrpc: "2.0", id: message.id, result });
  } catch (error) {
    writeMessage({
      jsonrpc: "2.0",
      id: message.id,
      error: { code: -32000, message: error.message || String(error) },
    });
  }
});
