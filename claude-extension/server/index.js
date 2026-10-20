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
      "List model-space entities with handle, type, layer and key geometry (line endpoints, polyline vertices, dimension measurement and override text, text content). Read-only. Set `units` to get lengths back in that unit (e.g. the unit the user is working in); without it values are in drawing units. The result's `units` field says which.",
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

function callAutoCAD(tool, args) {
  return new Promise((resolve, reject) => {
    const token = loadToken();
    const socket = net.createConnection({ host: HOST, port: PORT });
    let response = "";
    const timeout = setTimeout(() => {
      socket.destroy();
      reject(new Error("AutoCAD did not respond within 30 seconds."));
    }, 30000);

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
