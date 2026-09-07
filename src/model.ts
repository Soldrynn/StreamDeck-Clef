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
  bindingKind?: "amp-agent-process" | "amp-agent-alias";
}

export type RepeatMode = "off" | "all" | "one" | "unknown";

/** Controls reached through Apple Music's interface (UI Automation) rather than the media session. */
export interface UiState {
  available: boolean;
  shuffleActive?: boolean;
  repeatMode?: RepeatMode;
}

export interface BridgeState {
  type: "state";
  revision: number;
  timestampUtc: string;
  media: MediaState;
  audio: AudioState;
  ui?: UiState;
}

export interface PlaylistSettings {
  [key: string]: string | number | boolean | null | undefined;
  playlistId?: string;
  playlistName?: string;
  /** Set once the user edits or clears the title; the plugin then stops auto-filling it. */
  titleTouched?: boolean;
}

/** Title the Play Playlist key should display, or undefined to leave the user's own title in place. */
export function playlistAutoTitle(settings: PlaylistSettings): string | undefined {
  if (settings.titleTouched) return undefined;
  const name = settings.playlistName;
  return typeof name === "string" && name ? name : undefined;
}

/** Manifest state index for the Repeat key: 0 off, 1 all, 2 one. */
export function repeatStateIndex(mode: RepeatMode | undefined): number {
  return mode === "all" ? 1 : mode === "one" ? 2 : 0;
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
  data?: unknown;
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
  return {
    volumeStepPercent: clampInteger(settings.volumeStepPercent, 1, 10, DEFAULT_VOLUME_SETTINGS.volumeStepPercent)
  };
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

export function sampledNewPosition(previous: MediaState, next: MediaState): boolean {
  return next.positionMs !== previous.positionMs ||
    next.playbackStatus !== previous.playbackStatus ||
    next.title !== previous.title ||
    next.available !== previous.available;
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

export interface VolumeKeySettings {
  [key: string]: string | number | boolean | null | undefined;
  volumeStepPercent?: number;
}

export const DEFAULT_VOLUME_KEY_SETTINGS: Required<VolumeKeySettings> = {
  volumeStepPercent: 5
};

export function volumeKeySettings(settings: VolumeKeySettings): Required<VolumeKeySettings> {
  return {
    volumeStepPercent: clampInteger(settings.volumeStepPercent, 1, 10, DEFAULT_VOLUME_KEY_SETTINGS.volumeStepPercent)
  };
}
