import type { BridgeState } from "./model.ts";
import { formatTime, hasSelectedTrack, interpolatedPosition } from "./model.ts";

export type NowPlayingStatus = "playing" | "paused" | "idle";

export interface NowPlayingView {
  artworkDataUri?: string;
  title: string;
  subtitle: string;
  status: NowPlayingStatus;
  /** 0..1 */
  progress: number;
  time?: string;
}

const KEY_SIZE = 144;
const TITLE_MAX = 15;
const SUBTITLE_MAX = 22;
const ARTWORK_LIMIT = 40_000;

export function escapeXml(value: string): string {
  return value.replace(/[&<>"']/g, character => {
    switch (character) {
      case "&": return "&amp;";
      case "<": return "&lt;";
      case ">": return "&gt;";
      case '"': return "&quot;";
      default: return "&apos;";
    }
  });
}

export function truncateText(value: string, maxCharacters: number): string {
  const characters = Array.from(value.trim());
  if (characters.length <= maxCharacters) return characters.join("");
  return `${characters.slice(0, Math.max(1, maxCharacters - 1)).join("").trimEnd()}…`;
}

export function safeKeyArtwork(value: string | undefined): string | undefined {
  if (!value || value.length > ARTWORK_LIMIT) return undefined;
  return /^data:image\/(?:png|jpeg);base64,[A-Za-z0-9+/=]+$/i.test(value) ? value : undefined;
}

/** Builds the Now Playing key model from the current bridge state. */
export function nowPlayingView(state: BridgeState, connected: boolean, receivedAt: number, now = Date.now()): NowPlayingView {
  const media = state.media;
  if (!connected) {
    return { title: media.title || "Clef", subtitle: "Reconnecting…", status: "idle", progress: 0, artworkDataUri: safeKeyArtwork(media.artworkDataUri) };
  }
  if (!media.available) return { title: "Clef", subtitle: "Open Apple Music", status: "idle", progress: 0 };
  if (!hasSelectedTrack(media)) return { title: "Choose a song", subtitle: "Apple Music", status: "idle", progress: 0 };
  const duration = media.durationMs ?? 0;
  const position = interpolatedPosition(state, receivedAt, now);
  return {
    artworkDataUri: safeKeyArtwork(media.artworkDataUri),
    title: media.title || "Clef",
    subtitle: media.artist || media.album || "",
    status: media.playbackStatus === "playing" ? "playing" : "paused",
    progress: duration > 0 ? Math.max(0, Math.min(1, position / duration)) : 0,
    time: duration > 0 ? formatTime(position) : undefined
  };
}

/** Renders the Now Playing key as an SVG data URI that Stream Deck can display directly. */
export function nowPlayingImage(view: NowPlayingView): string {
  const artwork = view.artworkDataUri
    ? `<image href="${view.artworkDataUri}" xlink:href="${view.artworkDataUri}" x="0" y="0" width="${KEY_SIZE}" height="${KEY_SIZE}" preserveAspectRatio="xMidYMid slice"/>`
    : `<rect width="${KEY_SIZE}" height="${KEY_SIZE}" fill="url(#surface)"/>
  <circle cx="72" cy="58" r="30" fill="#FFFFFF" opacity=".06"/>
  <path d="M63 74V44l24-5v30" fill="none" stroke="#FFFFFF" stroke-width="3.4" stroke-linejoin="round" stroke-linecap="round" opacity=".9"/>
  <circle cx="56.5" cy="74.5" r="6.5" fill="#FFFFFF" opacity=".9"/><circle cx="80.5" cy="69" r="6.5" fill="#FFFFFF" opacity=".9"/>`;
  const badge = view.status === "idle"
    ? ""
    : `<circle cx="124" cy="20" r="12" fill="#0B0C0D" opacity=".78"/>${view.status === "playing"
      ? `<path d="M120 14.5 130 20l-10 5.5Z" fill="#FFFFFF"/>`
      : `<rect x="118.5" y="14.5" width="3.6" height="11" rx="1" fill="#FFFFFF"/><rect x="125.9" y="14.5" width="3.6" height="11" rx="1" fill="#FFFFFF"/>`}`;
  const barWidth = 124;
  const fill = Math.round(barWidth * view.progress);
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="${KEY_SIZE}" height="${KEY_SIZE}" viewBox="0 0 ${KEY_SIZE} ${KEY_SIZE}">
  <defs>
    <linearGradient id="surface" x1="20" y1="10" x2="124" y2="134" gradientUnits="userSpaceOnUse">
      <stop stop-color="#4A4D51"/><stop offset=".52" stop-color="#282A2D"/><stop offset="1" stop-color="#101113"/>
    </linearGradient>
    <linearGradient id="fade" x1="0" y1="70" x2="0" y2="144" gradientUnits="userSpaceOnUse">
      <stop stop-color="#000000" stop-opacity="0"/><stop offset=".45" stop-color="#000000" stop-opacity=".62"/><stop offset="1" stop-color="#000000" stop-opacity=".9"/>
    </linearGradient>
  </defs>
  <rect width="${KEY_SIZE}" height="${KEY_SIZE}" fill="#0B0C0D"/>
  ${artwork}
  <rect x="0" y="70" width="${KEY_SIZE}" height="74" fill="url(#fade)"/>
  ${badge}
  <text x="10" y="112" fill="#FFFFFF" font-family="Segoe UI, Helvetica, Arial, sans-serif" font-size="15" font-weight="700">${escapeXml(truncateText(view.title, TITLE_MAX))}</text>
  <text x="10" y="127" fill="#D2D2D2" font-family="Segoe UI, Helvetica, Arial, sans-serif" font-size="11" font-weight="500">${escapeXml(truncateText(view.subtitle, SUBTITLE_MAX))}</text>
  <rect x="10" y="135" width="${barWidth}" height="3" rx="1.5" fill="#FFFFFF" opacity=".22"/>
  ${fill > 0 ? `<rect x="10" y="135" width="${fill}" height="3" rx="1.5" fill="#E2E2E2"/>` : ""}
</svg>`;
  return `data:image/svg+xml;base64,${Buffer.from(svg, "utf8").toString("base64")}`;
}
