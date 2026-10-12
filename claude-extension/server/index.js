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

const tools = [
  {
    name: "autocad_status",
    description:
      "Check whether AutoCAD 2027 is connected and return the active drawing name. Use this before any drawing action.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
  },
  {
    name: "draw_line",
    description:
      "Draw exactly one straight line in the active AutoCAD drawing. Coordinates use the drawing's current units. Ask the user to confirm unclear coordinates or units before calling.",
    inputSchema: {
      type: "object",
      properties: {
        startX: { type: "number", description: "Starting X coordinate." },
        startY: { type: "number", description: "Starting Y coordinate." },
        endX: { type: "number", description: "Ending X coordinate." },
        endY: { type: "number", description: "Ending Y coordinate." },
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
      "Draw exactly one axis-aligned closed rectangle in the active AutoCAD drawing. Coordinates and dimensions use the drawing's current units. Ask the user to confirm unclear units before calling.",
    inputSchema: {
      type: "object",
      properties: {
        x: { type: "number", description: "Lower-left X coordinate." },
        y: { type: "number", description: "Lower-left Y coordinate." },
        width: { type: "number", exclusiveMinimum: 0, description: "Rectangle width." },
        height: { type: "number", exclusiveMinimum: 0, description: "Rectangle height." },
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
];

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
      serverInfo: { name: "autocad-local-bridge", version: "0.1.0" },
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
    if (!tools.some((tool) => tool.name === name)) {
      throw new Error("Unknown or disabled AutoCAD tool.");
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
