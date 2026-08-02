export type PlaybackStatus = "playing" | "paused" | "stopped" | "unknown";

export interface MediaState {
  available: boolean;
  sourceAppId?: string;
  title?: string;
  artist?: string;
  album?: string;
  playbackStatus?: PlaybackStatus;
  positionMs?: number;
  durationMs?: number;
  artworkDataUri?: string;
  artworkKey?: string;
}

export interface AudioState {
  available: boolean;
  volumePercent?: number;
  muted?: boolean;
  bindingKind?: "apple-music-process" | "amp-agent-process" | "amp-agent-alias";
}

export interface BridgeState {
  type: "state";
  revision: number;
  timestampUtc: string;
  media: MediaState;
  audio: AudioState;
}

export interface BridgeHello {
  type: "hello";
  protocol: 1;
  version: string;
}

export type BridgeMessage = BridgeState | BridgeHello | {
  type: "ack";
  id: number;
  ok: boolean;
  error?: string;
};

export interface PlaybackSettings {
  [key: string]: string | number | boolean | null | undefined;
}

export interface VolumeSettings {
  [key: string]: string | number | boolean | null | undefined;
  volumeStepPercent?: number;
}

export const DEFAULT_VOLUME_SETTINGS: Required<VolumeSettings> = {
  volumeStepPercent: 2
};

export function volumeSettings(settings: VolumeSettings): Required<VolumeSettings> {
  return { volumeStepPercent: clampInteger(settings.volumeStepPercent, 1, 10, 2) };
}

export function clampInteger(value: unknown, min: number, max: number, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value)
    ? Math.max(min, Math.min(max, Math.round(value)))
    : fallback;
}

export function adjustedVolume(current: number | undefined, delta: number): number | undefined {
  if (current === undefined || !Number.isFinite(current) || !Number.isFinite(delta)) return undefined;
  return Math.max(0, Math.min(100, current + delta));
}

export function formatTime(milliseconds: number | undefined): string {
  if (milliseconds === undefined || !Number.isFinite(milliseconds) || milliseconds < 0) return "--:--";
  const totalSeconds = Math.floor(milliseconds / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`
    : `${minutes}:${String(seconds).padStart(2, "0")}`;
}

export function interpolatedPosition(state: BridgeState, receivedAt: number, now = Date.now()): number {
  const media = state.media;
  const base = media.positionMs ?? 0;
  const advanced = media.playbackStatus === "playing" ? Math.max(0, now - receivedAt) : 0;
  return Math.min(media.durationMs ?? Number.MAX_SAFE_INTEGER, base + advanced);
}

export function hasSelectedTrack(media: MediaState): boolean {
  const title = media.title?.trim();
  return Boolean(
    (title && title.toLocaleLowerCase() !== "apple music") ||
    media.artist?.trim() ||
    media.album?.trim() ||
    (media.durationMs ?? 0) > 0
  );
}

export function marqueeText(value: string, width: number, elapsedMs: number): string {
  const characters = Array.from(value);
  if (characters.length <= width) return value;
  if (elapsedMs < 1_400) return characters.slice(0, width).join("");
  const spacer = Array.from("   •   ");
  const tape = [...characters, ...spacer];
  const step = Math.floor((elapsedMs - 1_400) / 350) % tape.length;
  return [...tape, ...tape].slice(step, step + width).join("");
}
