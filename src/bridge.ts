import { EventEmitter } from "node:events";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { sampledNewPosition, type BridgeMessage, type BridgeState } from "./model.js";

export type BridgeCommand = "toggle" | "next" | "previous" | "volume" | "toggleMute" | "refresh";

const EMPTY_STATE: BridgeState = {
  type: "state",
  revision: 0,
  timestampUtc: new Date(0).toISOString(),
  media: { available: false },
  audio: { available: false }
};

export class BridgeSupervisor extends EventEmitter {
  #child?: ChildProcessWithoutNullStreams;
  #buffer = "";
  #restartTimer?: NodeJS.Timeout;
  #heartbeatTimer?: NodeJS.Timeout;
  #stopping = false;
  #restartAttempt = 0;
  #nextCommandId = 1;
  #lastMessageAt = 0;
  #positionSampledAt = 0;
  #connected = false;
  #state: BridgeState = EMPTY_STATE;
  #lastStateRevision = 0;
  #pendingCommands = new Map<number, { name: BridgeCommand; createdAt: number }>();

  get state(): BridgeState { return this.#state; }
  get stateReceivedAt(): number { return this.#positionSampledAt; }
  get connected(): boolean { return this.#connected; }

  start(): void {
    this.#stopping = false;
    this.#spawn();
    this.#heartbeatTimer ??= setInterval(() => this.#heartbeat(), 10_000);
  }

  stop(): void {
    this.#stopping = true;
    if (this.#restartTimer) clearTimeout(this.#restartTimer);
    if (this.#heartbeatTimer) clearInterval(this.#heartbeatTimer);
    this.#restartTimer = undefined;
    this.#heartbeatTimer = undefined;
    this.#child?.kill();
    this.#child = undefined;
  }

  command(name: BridgeCommand, amount?: number): boolean {
    const child = this.#child;
    if (!child || !this.#connected || !child.stdin.writable || child.stdin.destroyed) return false;
    this.#prunePendingCommands();
    if (this.#pendingCommands.size >= 64) {
      this.emit("log", "Helper command queue reached its safety limit.");
      return false;
    }
    const id = this.#nextCommandId++;
    const payload = { type: "command", id, name, ...(amount === undefined ? {} : { amount }) };
    this.#pendingCommands.set(id, { name, createdAt: Date.now() });
    try {
      child.stdin.write(`${JSON.stringify(payload)}\n`, error => {
        if (error && this.#child === child) {
          this.#pendingCommands.delete(id);
          this.emit("log", `Helper command pipe failed: ${error.message}`);
          child.kill();
        }
      });
      return true;
    } catch (error) {
      this.#pendingCommands.delete(id);
      this.emit("log", `Helper command write failed: ${String(error)}`);
      child.kill();
      return false;
    }
  }

  #spawn(): void {
    if (this.#stopping || this.#child) return;
    const pluginRoot = dirname(dirname(fileURLToPath(import.meta.url)));
    const executable = join(pluginRoot, "helper", "ClefBridge.exe");

    try {
      const child = spawn(executable, ["--stdio"], {
        cwd: dirname(executable),
        windowsHide: true,
        stdio: ["pipe", "pipe", "pipe"]
      });
      this.#child = child;
      this.#buffer = "";
      child.stdout.setEncoding("utf8");
      child.stderr.setEncoding("utf8");
      child.stdout.on("data", chunk => this.#onData(String(chunk)));
      child.stderr.on("data", chunk => this.emit("log", String(chunk).trim()));
      child.stdin.on("error", error => {
        this.emit("log", `Helper input pipe failed: ${error.message}`);
        if (this.#child === child) child.kill();
      });
      child.once("error", error => this.emit("log", `Helper launch failed: ${error.message}`));
      child.once("exit", (code, signal) => this.#onExit(code, signal));
    } catch (error) {
      this.emit("log", `Helper launch failed: ${String(error)}`);
      this.#scheduleRestart();
    }
  }

  #onData(chunk: string): void {
    this.#buffer += chunk;
    if (this.#buffer.length > 1_000_000 && !this.#buffer.includes("\n")) {
      this.emit("log", "Helper output exceeded the protocol line limit; restarting it.");
      this.#child?.kill();
      return;
    }
    for (;;) {
      const newline = this.#buffer.indexOf("\n");
      if (newline < 0) break;
      const line = this.#buffer.slice(0, newline).trim();
      this.#buffer = this.#buffer.slice(newline + 1);
      if (!line) continue;
      try {
        const message = JSON.parse(line) as BridgeMessage;
        this.#lastMessageAt = Date.now();
        if (message.type === "hello") {
          if (message.protocol !== 1) throw new Error(`Unsupported helper protocol ${message.protocol}`);
          this.#lastStateRevision = 0;
          this.#positionSampledAt = 0;
          this.#connected = true;
          this.#restartAttempt = 0;
          this.emit("connection", true);
          this.command("refresh");
        } else if (message.type === "state") {
          if (message.revision <= this.#lastStateRevision) {
            this.emit("log", `Ignored stale helper state revision ${message.revision}.`);
            continue;
          }
          this.#lastStateRevision = message.revision;
          if (!message.media.artworkDataUri &&
              message.media.artworkKey &&
              message.media.artworkKey === this.#state.media.artworkKey) {
            message.media.artworkDataUri = this.#state.media.artworkDataUri;
          }
          if (this.#positionSampledAt === 0 || sampledNewPosition(this.#state.media, message.media)) {
            this.#positionSampledAt = this.#lastMessageAt;
          }
          this.#state = message;
          this.emit("state", message);
        } else {
          const name = this.#pendingCommands.get(message.id)?.name;
          this.#pendingCommands.delete(message.id);
          if (!message.ok) {
            const error = message.error ?? "unknown error";
            this.emit("log", `Command ${message.id} failed: ${error}`);
            this.emit("commandError", { name, error });
          }
        }
      } catch (error) {
        this.emit("log", `Ignored malformed helper output: ${String(error)}`);
      }
    }
  }

  #onExit(code: number | null, signal: NodeJS.Signals | null): void {
    this.#child = undefined;
    this.#connected = false;
    this.#pendingCommands.clear();
    this.emit("connection", false);
    if (!this.#stopping) {
      this.emit("log", `Helper exited (${code ?? signal ?? "unknown"}); restarting.`);
      this.#scheduleRestart();
    }
  }

  #scheduleRestart(): void {
    if (this.#stopping || this.#restartTimer) return;
    const delay = Math.min(5_000, 250 * 2 ** Math.min(this.#restartAttempt++, 5));
    this.#restartTimer = setTimeout(() => {
      this.#restartTimer = undefined;
      this.#spawn();
    }, delay);
  }

  #heartbeat(): void {
    if (this.#stopping) return;
    if (!this.#child) {
      this.#spawn();
      return;
    }
    this.#prunePendingCommands();
    if (this.#connected && Date.now() - this.#lastMessageAt > 25_000) {
      this.emit("log", "Helper stopped responding; restarting it.");
      this.#child.kill();
      return;
    }
    this.command("refresh");
  }

  #prunePendingCommands(): void {
    const cutoff = Date.now() - 30_000;
    for (const [id, command] of this.#pendingCommands) {
      if (command.createdAt < cutoff) this.#pendingCommands.delete(id);
    }
  }
}
