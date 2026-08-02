import { spawn } from "node:child_process";
import { once } from "node:events";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { WebSocketServer } from "ws";

const pluginUuid = "com.davedev.clef";
const bundle = join(process.cwd(), "com.davedev.clef.sdPlugin", "bin", "plugin.js");
const pluginRoot = join(process.cwd(), "com.davedev.clef.sdPlugin");
const manifest = JSON.parse(readFileSync(join(pluginRoot, "manifest.json"), "utf8"));
let output = "";
const server = new WebSocketServer({ host: "127.0.0.1", port: 0 });
await once(server, "listening");
const address = server.address();
if (typeof address === "string" || !address) throw new Error("Smoke-test WebSocket server has no TCP address.");

let acceptRegistration;
let rejectRegistration;
const registration = new Promise((resolve, reject) => {
  acceptRegistration = resolve;
  rejectRegistration = reject;
});

server.on("connection", socket => {
  output += "[smoke] WebSocket connected.\n";
  socket.on("message", data => {
    try {
      const message = JSON.parse(String(data));
      output += `[smoke] Received ${JSON.stringify(message)}.\n`;
      if (message.event === "registerPlugin" && message.uuid === pluginUuid) acceptRegistration(message);
    } catch (error) {
      rejectRegistration(error);
    }
  });
});

const info = JSON.stringify({
  application: { language: "en", platform: "windows", platformVersion: "11", version: "7.1.0" },
  plugin: { uuid: pluginUuid, version: manifest.Version },
  devices: [],
  devicePixelRatio: 1
});
const child = spawn(process.execPath, [
  bundle,
  "-port", String(address.port),
  "-pluginUUID", pluginUuid,
  "-registerEvent", "registerPlugin",
  "-info", info
], { cwd: pluginRoot, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
child.stdout.setEncoding("utf8");
child.stderr.setEncoding("utf8");
child.stdout.on("data", chunk => { output += chunk; });
child.stderr.on("data", chunk => { output += chunk; });

try {
  await Promise.race([
    registration,
    once(child, "exit").then(([code]) => { throw new Error(`Plugin exited with ${code}.\n${output}`); }),
    new Promise((_, reject) => setTimeout(() => reject(new Error(`Plugin did not register within 3 seconds.\n${output}`)), 3_000))
  ]);
} finally {
  child.kill();
  await Promise.race([once(child, "exit"), new Promise(resolve => setTimeout(resolve, 1_000))]);
  await new Promise(resolve => server.close(resolve));
}
