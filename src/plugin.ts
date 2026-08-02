import streamDeck, {
  action,
  DialDownEvent,
  DialRotateEvent,
  DidReceiveSettingsEvent,
  SingletonAction,
  TouchTapEvent,
  WillAppearEvent,
  WillDisappearEvent
} from "@elgato/streamdeck";
import { BridgeSupervisor } from "./bridge.js";
import { LatestValuePump } from "./latest-value-pump.js";
import {
  adjustedVolume,
  formatTime,
  hasSelectedTrack,
  interpolatedPosition,
  marqueeText,
  type BridgeState,
  type PlaybackSettings,
  type VolumeSettings,
  volumeSettings
} from "./model.js";
import { TickCoalescer } from "./tick-coalescer.js";

const bridge = new BridgeSupervisor();
const playbackTargets = new Map<string, any>();
const volumeTargets = new Map<string, any>();
const artworkByTarget = new Map<string, string | undefined>();
const marqueeByTarget = new Map<string, { key: string; startedAt: number }>();
let optimisticVolume: { value: number; originRevision: number; expiresAt: number } | undefined;
let optimisticVolumeTimer: NodeJS.Timeout | undefined;
type FeedbackUpdate = { target: any; feedback: Record<string, string | number | object> };
const feedbackPumps = new Map<string, LatestValuePump<FeedbackUpdate>>();

bridge.on("log", message => streamDeck.logger.info(String(message)));
bridge.on("state", () => {
  if (optimisticVolume && bridge.state.revision > optimisticVolume.originRevision) {
    const actual = bridge.state.audio.volumePercent;
    if (actual !== undefined && Math.abs(actual - optimisticVolume.value) <= 1) clearOptimisticVolume();
    else if (Date.now() >= optimisticVolume.expiresAt) clearOptimisticVolume();
  }
  renderAll();
});
bridge.on("connection", (connected: boolean) => {
  if (!connected) {
    artworkByTarget.clear();
    clearOptimisticVolume();
  }
  renderAll();
});
bridge.on("commandError", ({ name }: { name?: string }) => {
  const targets = name === "volume" || name === "toggleMute" ? volumeTargets : playbackTargets;
  if (name === "volume") clearOptimisticVolume();
  for (const target of targets.values()) void target.showAlert();
});

const progressTimer = setInterval(() => {
  if (playbackTargets.size > 0 && bridge.connected &&
      (bridge.state.media.playbackStatus === "playing" || hasOverflowingText())) renderPlaybackTargets();
}, 350);
progressTimer.unref();

function renderAll(): void {
  renderPlaybackTargets();
  renderVolumeTargets();
}

function queueFeedback(id: string, target: any, feedback: Record<string, string | number | object>): void {
  let pump = feedbackPumps.get(id);
  if (!pump) {
    pump = new LatestValuePump(
      update => update.target.setFeedback(update.feedback),
      error => streamDeck.logger.info(`Feedback update failed: ${String(error)}`)
    );
    feedbackPumps.set(id, pump);
  }
  pump.submit({ target, feedback });
}

function renderPlaybackTargets(): void {
  const state = bridge.state;
  for (const [id, target] of playbackTargets) {
    const media = state.media;
    if (!bridge.connected) {
      const position = interpolatedPosition(state, bridge.stateReceivedAt);
      const duration = media.durationMs ?? 0;
      queueFeedback(id, target, {
        art: safeArtwork(media.artworkDataUri) || "assets/placeholders/album.svg",
        status: "RECONNECTING",
        track: media.title || "Clef",
        artist: media.artist || "Restoring connection…",
        progress: duration > 0 ? Math.round(position / duration * 1000) : 0,
        time: media.available ? `${formatTime(position)}  /  ${formatTime(media.durationMs)}` : "--:--  /  --:--"
      });
      continue;
    }
    if (!media.available) {
      queueFeedback(id, target, {
        art: "assets/placeholders/album.svg",
        status: "UNAVAILABLE",
        track: "Clef",
        artist: "Open Apple Music for Windows to connect",
        progress: 0,
        time: "--:--  /  --:--"
      });
      continue;
    }

    if (!hasSelectedTrack(media)) {
      artworkByTarget.delete(id);
      marqueeByTarget.delete(id);
      queueFeedback(id, target, {
        art: "assets/placeholders/album.svg",
        status: "READY",
        track: "Choose a song",
        artist: "Apple Music for Windows",
        progress: 0,
        time: "--:--  /  --:--"
      });
      continue;
    }

    const position = interpolatedPosition(state, bridge.stateReceivedAt);
    const duration = media.durationMs ?? 0;
    const marqueeKey = media.title ?? "";
    let marquee = marqueeByTarget.get(id);
    if (!marquee || marquee.key !== marqueeKey) {
      marquee = { key: marqueeKey, startedAt: Date.now() };
      marqueeByTarget.set(id, marquee);
    }
    const elapsed = Date.now() - marquee.startedAt;
    const feedback: Record<string, string | number | object> = {
      status: media.playbackStatus === "playing" ? "NOW PLAYING" : media.playbackStatus === "paused" ? "PAUSED" : "READY",
      track: marqueeText(media.title || "Clef", 16, elapsed),
      artist: media.artist || media.album || "",
      progress: duration > 0 ? Math.round(position / duration * 1000) : 0,
      time: `${formatTime(position)}  /  ${formatTime(media.durationMs)}`
    };
    if (artworkByTarget.get(id) !== media.artworkKey) {
      feedback.art = safeArtwork(media.artworkDataUri) || "assets/placeholders/album.svg";
      artworkByTarget.set(id, media.artworkKey);
    }
    queueFeedback(id, target, feedback);
  }
}

function hasOverflowingText(): boolean {
  const media = bridge.state.media;
  return (media.title?.length ?? 0) > 16;
}

function safeArtwork(value: string | undefined): string | undefined {
  if (!value || value.length > 40_000) return undefined;
  return /^data:image\/(?:png|jpeg);base64,/i.test(value) ? value : undefined;
}

function renderVolumeTargets(): void {
  const audio = bridge.state.audio;
  for (const [id, target] of volumeTargets) {
    if (!bridge.connected) {
      queueFeedback(id, target, { status: "RECONNECTING", value: "--%", detail: "Restoring connection", volume: 0 });
    } else if (!audio.available) {
      queueFeedback(id, target, { status: "UNAVAILABLE", value: "--%", detail: "Start playing a song", volume: 0 });
    } else {
      const reportedVolume = optimisticVolume && Date.now() < optimisticVolume.expiresAt
        ? optimisticVolume.value
        : audio.volumePercent ?? 0;
      const volume = Math.max(0, Math.min(100, Math.round(reportedVolume)));
      queueFeedback(id, target, {
        status: "VOLUME",
        value: audio.muted ? "Muted" : `${volume}%`,
        detail: audio.muted ? "Press to unmute" : "Press to mute",
        volume
      });
    }
  }
}

function showOptimisticVolume(delta: number): void {
  const now = Date.now();
  const current = optimisticVolume && now < optimisticVolume.expiresAt
    ? optimisticVolume.value
    : bridge.state.audio.volumePercent;
  const value = adjustedVolume(current, delta);
  if (value === undefined) return;
  optimisticVolume = {
    value,
    originRevision: bridge.state.revision,
    expiresAt: now + 5_000
  };
  if (optimisticVolumeTimer) clearTimeout(optimisticVolumeTimer);
  optimisticVolumeTimer = setTimeout(() => {
    optimisticVolumeTimer = undefined;
    clearOptimisticVolume();
    renderVolumeTargets();
  }, 5_000);
  renderVolumeTargets();
}

function clearOptimisticVolume(): void {
  optimisticVolume = undefined;
  if (optimisticVolumeTimer) clearTimeout(optimisticVolumeTimer);
  optimisticVolumeTimer = undefined;
}

@action({ UUID: "com.davedev.clef.playback" })
class PlaybackAction extends SingletonAction<PlaybackSettings> {
  readonly #trackCoalescers = new Map<string, TickCoalescer>();

  override onWillAppear(ev: WillAppearEvent<PlaybackSettings>): void {
    if (!ev.action.isDial()) return;
    const id = ev.action.id;
    playbackTargets.set(id, ev.action);
    this.#trackCoalescers.set(id, new TickCoalescer(ticks => this.#flushTrack(id, ticks), 160));
    renderPlaybackTargets();
  }

  override onWillDisappear(ev: WillDisappearEvent<PlaybackSettings>): void {
    const id = ev.action.id;
    this.#trackCoalescers.get(id)?.dispose();
    this.#trackCoalescers.delete(id);
    playbackTargets.delete(id);
    artworkByTarget.delete(id);
    marqueeByTarget.delete(id);
    feedbackPumps.get(id)?.close();
    feedbackPumps.delete(id);
  }

  override onDialRotate(ev: DialRotateEvent<PlaybackSettings>): void {
    this.#trackCoalescers.get(ev.action.id)?.add(ev.payload.ticks);
  }

  override onDialDown(ev: DialDownEvent<PlaybackSettings>): void {
    if (!bridge.command("toggle")) void ev.action.showAlert();
  }

  override onTouchTap(ev: TouchTapEvent<PlaybackSettings>): void {
    if (!bridge.command("toggle")) void ev.action.showAlert();
  }

  #flushTrack(id: string, ticks: number): void {
    if (!bridge.command(ticks > 0 ? "next" : "previous")) void playbackTargets.get(id)?.showAlert();
  }
}

@action({ UUID: "com.davedev.clef.volume" })
class VolumeAction extends SingletonAction<VolumeSettings> {
  readonly #coalescers = new Map<string, TickCoalescer>();
  readonly #settings = new Map<string, Required<VolumeSettings>>();

  override onWillAppear(ev: WillAppearEvent<VolumeSettings>): void {
    if (!ev.action.isDial()) return;
    const id = ev.action.id;
    volumeTargets.set(id, ev.action);
    this.#settings.set(id, volumeSettings(ev.payload.settings));
    this.#coalescers.set(id, new TickCoalescer(ticks => this.#flush(id, ticks)));
    renderVolumeTargets();
  }

  override onWillDisappear(ev: WillDisappearEvent<VolumeSettings>): void {
    const id = ev.action.id;
    this.#coalescers.get(id)?.dispose();
    this.#coalescers.delete(id);
    this.#settings.delete(id);
    volumeTargets.delete(id);
    feedbackPumps.get(id)?.close();
    feedbackPumps.delete(id);
  }

  override onDidReceiveSettings(ev: DidReceiveSettingsEvent<VolumeSettings>): void {
    const id = ev.action.id;
    const settings = volumeSettings(ev.payload.settings);
    this.#settings.set(id, settings);
    if (ev.action.isDial()) {
      void ev.action.setTriggerDescription({
        push: "Mute / Unmute Apple Music for Windows",
        rotate: `Adjust Apple Music for Windows volume ±${settings.volumeStepPercent}%`,
        touch: "Mute / Unmute Apple Music for Windows"
      });
    }
  }

  override onDialRotate(ev: DialRotateEvent<VolumeSettings>): void {
    this.#coalescers.get(ev.action.id)?.add(ev.payload.ticks);
  }

  override onDialDown(ev: DialDownEvent<VolumeSettings>): void {
    if (!bridge.command("toggleMute")) void ev.action.showAlert();
  }

  override onTouchTap(ev: TouchTapEvent<VolumeSettings>): void {
    if (!bridge.command("toggleMute")) void ev.action.showAlert();
  }

  #flush(id: string, ticks: number): void {
    const settings = this.#settings.get(id) ?? volumeSettings({});
    const delta = ticks * settings.volumeStepPercent;
    showOptimisticVolume(delta);
    if (!bridge.command("volume", delta)) {
      clearOptimisticVolume();
      renderVolumeTargets();
      void volumeTargets.get(id)?.showAlert();
    }
  }
}

streamDeck.actions.registerAction(new PlaybackAction());
streamDeck.actions.registerAction(new VolumeAction());
bridge.start();

process.once("exit", () => bridge.stop());
process.once("SIGTERM", () => {
  bridge.stop();
  process.exit(0);
});

await streamDeck.connect();
