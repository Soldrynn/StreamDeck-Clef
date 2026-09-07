import streamDeck, {
  SingletonAction,
  type DidReceiveSettingsEvent,
  type KeyDownEvent,
  type KeyUpEvent,
  type SendToPluginEvent,
  type TitleParametersDidChangeEvent,
  type WillAppearEvent,
  type WillDisappearEvent
} from "@elgato/streamdeck";
import type { BridgeCommand, BridgeSupervisor } from "./bridge.ts";
import { HoldRepeater } from "./hold-repeater.ts";
import { nowPlayingImage, nowPlayingView } from "./key-image.ts";
import { playlistAutoTitle, type PlaylistSettings, repeatStateIndex, type VolumeKeySettings, volumeKeySettings } from "./model.ts";

export const KEY_ACTIONS = {
  playPause: "com.davedev.clef.key.playpause",
  next: "com.davedev.clef.key.next",
  previous: "com.davedev.clef.key.previous",
  volumeUp: "com.davedev.clef.key.volumeup",
  volumeDown: "com.davedev.clef.key.volumedown",
  mute: "com.davedev.clef.key.mute",
  nowPlaying: "com.davedev.clef.key.nowplaying",
  shuffle: "com.davedev.clef.key.shuffle",
  repeat: "com.davedev.clef.key.repeat",
  favorite: "com.davedev.clef.key.favorite",
  playlist: "com.davedev.clef.key.playlist"
} as const;

/** Datasource name the playlist inspector's dropdown asks the plugin for. */
export const PLAYLIST_DATASOURCE = "getPlaylists";

export interface KeyHost {
  bridge: BridgeSupervisor;
  /** Sends a relative volume change, including optimistic dial feedback. */
  adjustVolume(delta: number): boolean;
}

export interface KeyController {
  /** Re-renders every visible key from the current bridge state. */
  render(): void;
  /** Advances time-based key visuals (Now Playing progress). Cheap to call often. */
  tick(): void;
}

type KeySettings = Record<string, string | number | boolean | null | undefined>;
const NOW_PLAYING_TICK_MS = 1_000;
const TITLE_SETTLE_MS = 1_500;

/** Shapes the helper's playlist reply into sdpi-components datasource items. */
export function playlistItems(data: unknown): { label: string; value: string }[] {
  if (!Array.isArray(data)) return [];
  const items: { label: string; value: string }[] = [];
  for (const entry of data) {
    if (!entry || typeof entry !== "object") continue;
    const { id, name } = entry as { id?: unknown; name?: unknown };
    if (typeof id === "string" && id && typeof name === "string" && name) items.push({ label: name, value: id });
  }
  return items;
}

export function registerKeyActions(host: KeyHost): KeyController {
  const { bridge } = host;
  const playPauseTargets = new Map<string, any>();
  const muteTargets = new Map<string, any>();
  const nowPlayingTargets = new Map<string, any>();
  const volumeTargets = new Map<string, any>();
  const transportTargets = new Map<string, any>();
  const shuffleTargets = new Map<string, any>();
  const repeatTargets = new Map<string, any>();
  const favoriteTargets = new Map<string, any>();
  const playlistTargets = new Map<string, any>();
  const lastState = new Map<string, number>();
  const lastImage = new Map<string, string>();
  let lastNowPlayingRender = 0;

  function send(target: any, command: BridgeCommand, amount?: number, targetId?: string): void {
    if (!bridge.command(command, amount, targetId)) void target.showAlert();
  }

  function setState(target: any, id: string, state: number): void {
    if (lastState.get(id) === state) return;
    lastState.set(id, state);
    target.setState(state).catch((error: unknown) => streamDeck.logger.info(`Key state update failed: ${String(error)}`));
  }

  function renderPlayPause(): void {
    const playing = bridge.connected && bridge.state.media.available && bridge.state.media.playbackStatus === "playing";
    for (const [id, target] of playPauseTargets) setState(target, id, playing ? 1 : 0);
  }

  function renderMute(): void {
    const muted = bridge.connected && bridge.state.audio.available && bridge.state.audio.muted === true;
    for (const [id, target] of muteTargets) setState(target, id, muted ? 1 : 0);
  }

  function renderShuffle(): void {
    const ui = bridge.state.ui;
    const active = bridge.connected && ui?.available === true && ui.shuffleActive === true;
    for (const [id, target] of shuffleTargets) setState(target, id, active ? 1 : 0);
  }

  function renderRepeat(): void {
    const ui = bridge.state.ui;
    const state = bridge.connected && ui?.available ? repeatStateIndex(ui.repeatMode) : 0;
    for (const [id, target] of repeatTargets) setState(target, id, state);
  }

  function renderNowPlaying(): void {
    if (nowPlayingTargets.size === 0) return;
    lastNowPlayingRender = Date.now();
    const view = nowPlayingView(bridge.state, bridge.connected, bridge.stateReceivedAt);
    const image = nowPlayingImage(view);
    for (const [id, target] of nowPlayingTargets) {
      if (lastImage.get(id) === image) continue;
      lastImage.set(id, image);
      target.setImage(image).catch((error: unknown) => streamDeck.logger.info(`Now Playing image update failed: ${String(error)}`));
    }
  }

  function forget(id: string): void {
    for (const map of [
      playPauseTargets, muteTargets, nowPlayingTargets, volumeTargets, transportTargets,
      shuffleTargets, repeatTargets, favoriteTargets, playlistTargets
    ]) map.delete(id);
    lastState.delete(id);
    lastImage.delete(id);
  }

  class KeyAction<T extends KeySettings = KeySettings> extends SingletonAction<T> {
    override readonly manifestId: string;
    readonly #targets: Map<string, any>;
    readonly #render: () => void;
    constructor(manifestId: string, targets: Map<string, any>, render: () => void) {
      super();
      this.manifestId = manifestId;
      this.#targets = targets;
      this.#render = render;
    }
    override onWillAppear(ev: WillAppearEvent<T>): void {
      if (!ev.action.isKey()) return;
      this.#targets.set(ev.action.id, ev.action);
      this.#render();
    }
    override onWillDisappear(ev: WillDisappearEvent<T>): void {
      forget(ev.action.id);
    }
  }

  class CommandKey extends KeyAction {
    readonly #command: BridgeCommand;
    constructor(manifestId: string, targets: Map<string, any>, render: () => void, command: BridgeCommand) {
      super(manifestId, targets, render);
      this.#command = command;
    }
    override onKeyDown(ev: KeyDownEvent<KeySettings>): void {
      send(ev.action, this.#command);
    }
  }

  /** A key whose command is confirmed with a checkmark, because it has no visible state to reflect. */
  class ConfirmedKey extends KeyAction {
    readonly #command: BridgeCommand;
    constructor(manifestId: string, targets: Map<string, any>, command: BridgeCommand) {
      super(manifestId, targets, () => undefined);
      this.#command = command;
    }
    override onKeyDown(ev: KeyDownEvent<KeySettings>): void {
      bridge.request(this.#command).then(
        () => ev.action.showOk(),
        (error: unknown) => {
          streamDeck.logger.info(`${this.#command} failed: ${String(error)}`);
          return ev.action.showAlert();
        }
      );
    }
  }

  class VolumeKey extends KeyAction<VolumeKeySettings> {
    readonly #repeaters = new Map<string, HoldRepeater>();
    readonly #settings = new Map<string, Required<VolumeKeySettings>>();
    readonly #direction: 1 | -1;
    constructor(manifestId: string, direction: 1 | -1) {
      super(manifestId, volumeTargets, () => undefined);
      this.#direction = direction;
    }
    override onWillAppear(ev: WillAppearEvent<VolumeKeySettings>): void {
      super.onWillAppear(ev);
      if (!ev.action.isKey()) return;
      this.#settings.set(ev.action.id, volumeKeySettings(ev.payload.settings));
    }
    override onWillDisappear(ev: WillDisappearEvent<VolumeKeySettings>): void {
      this.#repeaters.get(ev.action.id)?.dispose();
      this.#repeaters.delete(ev.action.id);
      this.#settings.delete(ev.action.id);
      super.onWillDisappear(ev);
    }
    override onDidReceiveSettings(ev: DidReceiveSettingsEvent<VolumeKeySettings>): void {
      this.#settings.set(ev.action.id, volumeKeySettings(ev.payload.settings));
    }
    override onKeyDown(ev: KeyDownEvent<VolumeKeySettings>): void {
      const id = ev.action.id;
      let repeater = this.#repeaters.get(id);
      if (!repeater) {
        repeater = new HoldRepeater(() => {
          const step = (this.#settings.get(id) ?? volumeKeySettings({})).volumeStepPercent;
          if (!host.adjustVolume(this.#direction * step)) {
            repeater?.release();
            void ev.action.showAlert();
          }
        });
        this.#repeaters.set(id, repeater);
      }
      repeater.press();
    }
    override onKeyUp(ev: KeyUpEvent<VolumeKeySettings>): void {
      this.#repeaters.get(ev.action.id)?.release();
    }
  }

  /**
   * Starts a chosen library playlist. The key auto-fills its title with the playlist name until the
   * user edits or clears the title; after that the user's title (even an empty one) wins. Choosing a
   * different playlist re-arms the auto-fill.
   */
  class PlaylistKey extends KeyAction<PlaylistSettings> {
    readonly #settings = new Map<string, PlaylistSettings>();
    readonly #appearedAt = new Map<string, number>();
    constructor() {
      super(KEY_ACTIONS.playlist, playlistTargets, () => undefined);
    }
    override onWillAppear(ev: WillAppearEvent<PlaylistSettings>): void {
      super.onWillAppear(ev);
      if (!ev.action.isKey()) return;
      this.#settings.set(ev.action.id, ev.payload.settings);
      this.#appearedAt.set(ev.action.id, Date.now());
      this.#applyTitle(ev.action);
    }
    override onWillDisappear(ev: WillDisappearEvent<PlaylistSettings>): void {
      this.#settings.delete(ev.action.id);
      this.#appearedAt.delete(ev.action.id);
      super.onWillDisappear(ev);
    }
    override onDidReceiveSettings(ev: DidReceiveSettingsEvent<PlaylistSettings>): void {
      const previous = this.#settings.get(ev.action.id);
      let settings = ev.payload.settings;
      if (previous && settings.playlistId !== previous.playlistId && settings.titleTouched) {
        settings = { ...settings, titleTouched: false };
        void ev.action.setSettings(settings);
      }
      this.#settings.set(ev.action.id, settings);
      this.#applyTitle(ev.action);
    }
    override onTitleParametersDidChange(ev: TitleParametersDidChangeEvent<PlaylistSettings>): void {
      // Stream Deck also reports title parameters right after a key appears; only a change made
      // while the key is already on screen counts as the user touching the title.
      const appearedAt = this.#appearedAt.get(ev.action.id) ?? 0;
      if (Date.now() - appearedAt < TITLE_SETTLE_MS) return;
      const settings = this.#settings.get(ev.action.id) ?? {};
      if (settings.titleTouched) return;
      const touched = { ...settings, titleTouched: true };
      this.#settings.set(ev.action.id, touched);
      void ev.action.setSettings(touched);
      this.#applyTitle(ev.action);
    }
    override onSendToPlugin(ev: SendToPluginEvent<any, PlaylistSettings>): void {
      if (ev.payload?.event !== PLAYLIST_DATASOURCE) return;
      bridge.request("listPlaylists").then(
        data => streamDeck.ui.sendToPropertyInspector({ event: PLAYLIST_DATASOURCE, items: playlistItems(data) }),
        (error: unknown) => {
          streamDeck.logger.info(`Playlist list failed: ${String(error)}`);
          return streamDeck.ui.sendToPropertyInspector({ event: PLAYLIST_DATASOURCE, items: [] });
        }
      );
    }
    override onKeyDown(ev: KeyDownEvent<PlaylistSettings>): void {
      const id = this.#settings.get(ev.action.id)?.playlistId;
      if (typeof id !== "string" || !id) {
        void ev.action.showAlert();
        return;
      }
      bridge.request("playPlaylist", { target: id }).then(
        () => ev.action.showOk(),
        (error: unknown) => {
          streamDeck.logger.info(`Playlist start failed: ${String(error)}`);
          return ev.action.showAlert();
        }
      );
    }
    #applyTitle(action: any): void {
      if (!action.isKey()) return;
      const title = playlistAutoTitle(this.#settings.get(action.id) ?? {});
      action.setTitle(title).catch((error: unknown) => streamDeck.logger.info(`Playlist title update failed: ${String(error)}`));
    }
  }

  const actions: SingletonAction<any>[] = [
    new CommandKey(KEY_ACTIONS.playPause, playPauseTargets, renderPlayPause, "toggle"),
    new CommandKey(KEY_ACTIONS.next, transportTargets, () => undefined, "next"),
    new CommandKey(KEY_ACTIONS.previous, transportTargets, () => undefined, "previous"),
    new CommandKey(KEY_ACTIONS.mute, muteTargets, renderMute, "toggleMute"),
    new CommandKey(KEY_ACTIONS.nowPlaying, nowPlayingTargets, renderNowPlaying, "toggle"),
    new CommandKey(KEY_ACTIONS.shuffle, shuffleTargets, renderShuffle, "shuffle"),
    new CommandKey(KEY_ACTIONS.repeat, repeatTargets, renderRepeat, "repeat"),
    new ConfirmedKey(KEY_ACTIONS.favorite, favoriteTargets, "favorite"),
    new PlaylistKey(),
    new VolumeKey(KEY_ACTIONS.volumeUp, 1),
    new VolumeKey(KEY_ACTIONS.volumeDown, -1)
  ];
  for (const action of actions) streamDeck.actions.registerAction(action);

  bridge.on("commandError", ({ name }: { name?: string }) => {
    const targets = name === "volume" ? [...volumeTargets.values()]
      : name === "toggleMute" ? [...muteTargets.values()]
      : name === "toggle" ? [...playPauseTargets.values(), ...nowPlayingTargets.values()]
      : name === "shuffle" ? [...shuffleTargets.values()]
      : name === "repeat" ? [...repeatTargets.values()]
      : name === "next" || name === "previous" ? [...transportTargets.values()]
      : [];
    for (const target of targets) void target.showAlert();
  });

  return {
    render(): void {
      renderPlayPause();
      renderMute();
      renderShuffle();
      renderRepeat();
      renderNowPlaying();
    },
    tick(): void {
      if (nowPlayingTargets.size === 0 || !bridge.connected) return;
      if (bridge.state.media.playbackStatus !== "playing") return;
      if (Date.now() - lastNowPlayingRender >= NOW_PLAYING_TICK_MS) renderNowPlaying();
    }
  };
}
