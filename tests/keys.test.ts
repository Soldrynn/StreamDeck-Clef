import assert from "node:assert/strict";
import test from "node:test";
import { HoldRepeater } from "../src/hold-repeater.ts";
import { escapeXml, nowPlayingImage, nowPlayingView, safeKeyArtwork, truncateText } from "../src/key-image.ts";
import { volumeKeySettings, type BridgeState } from "../src/model.ts";

const PNG = "data:image/png;base64,iVBORw0KGgo=";

function state(overrides: Partial<BridgeState["media"]> = {}): BridgeState {
  return {
    type: "state", revision: 1, timestampUtc: new Date().toISOString(),
    media: { available: true, title: "Nutshell", artist: "Alice In Chains", playbackStatus: "playing", positionMs: 60_000, durationMs: 240_000, artworkDataUri: PNG, ...overrides },
    audio: { available: true, volumePercent: 40, muted: false }
  };
}

function decode(image: string): string {
  assert.match(image, /^data:image\/svg\+xml;base64,/);
  return Buffer.from(image.slice(image.indexOf(",") + 1), "base64").toString("utf8");
}

test("volume key settings default to a larger step than the dial and stay clamped", () => {
  assert.deepEqual(volumeKeySettings({}), { volumeStepPercent: 5 });
  assert.deepEqual(volumeKeySettings({ volumeStepPercent: 40 }), { volumeStepPercent: 10 });
  assert.deepEqual(volumeKeySettings({ volumeStepPercent: "3" as unknown as number }), { volumeStepPercent: 5 });
});

test("key text is escaped and truncated for the SVG renderer", () => {
  assert.equal(escapeXml(`Rock & Roll <"'>`), "Rock &amp; Roll &lt;&quot;&apos;&gt;");
  assert.equal(truncateText("Short", 15), "Short");
  assert.equal(truncateText("A very long song title indeed", 15), "A very long so…");
  assert.equal(Array.from(truncateText("ééééééééééééééééééé", 6)).length, 6);
});

test("only compact PNG or JPEG artwork reaches the key image", () => {
  assert.equal(safeKeyArtwork(PNG), PNG);
  assert.equal(safeKeyArtwork("data:image/svg+xml;base64,PHN2Zz4="), undefined);
  assert.equal(safeKeyArtwork(`data:image/png;base64,${"A".repeat(50_000)}`), undefined);
  assert.equal(safeKeyArtwork("data:image/png;base64,abc\"/><script>"), undefined);
});

test("the Now Playing view follows connection, availability, and playback state", () => {
  const playing = nowPlayingView(state(), true, 1_000, 11_000);
  assert.equal(playing.status, "playing");
  assert.equal(playing.title, "Nutshell");
  assert.equal(playing.subtitle, "Alice In Chains");
  assert.ok(Math.abs(playing.progress - 70_000 / 240_000) < 1e-9);
  assert.equal(playing.time, "1:10");
  assert.equal(playing.artworkDataUri, PNG);

  assert.equal(nowPlayingView(state({ playbackStatus: "paused" }), true, 0).status, "paused");
  assert.deepEqual(nowPlayingView(state({ available: false }), true, 0).subtitle, "Open Apple Music");
  assert.equal(nowPlayingView(state({ title: "Apple Music", artist: undefined, album: undefined, durationMs: 0 }), true, 0).title, "Choose a song");
  const reconnecting = nowPlayingView(state(), false, 0);
  assert.equal(reconnecting.status, "idle");
  assert.equal(reconnecting.subtitle, "Reconnecting…");
});

test("the Now Playing image embeds artwork, text, and progress", () => {
  const svg = decode(nowPlayingImage({ artworkDataUri: PNG, title: "Rock & Roll", subtitle: "Led Zeppelin", status: "playing", progress: 0.5 }));
  assert.ok(svg.includes(`href="${PNG}"`));
  assert.ok(svg.includes("Rock &amp; Roll"));
  assert.ok(svg.includes(`width="62" height="3"`), "progress fill is half of the 124px bar");

  const placeholder = decode(nowPlayingImage({ title: "Clef", subtitle: "Open Apple Music", status: "idle", progress: 0 }));
  assert.ok(!placeholder.includes("<image"));
  assert.ok(placeholder.includes("url(#surface)"));
});

test("a held key fires once immediately, then repeats until released", ctx => {
  ctx.mock.timers.enable({ apis: ["setTimeout", "setInterval"] });
  let fired = 0;
  const repeater = new HoldRepeater(() => { fired++; }, 450, 150);
  repeater.press();
  assert.equal(fired, 1);
  ctx.mock.timers.tick(449);
  assert.equal(fired, 1);
  ctx.mock.timers.tick(1);
  ctx.mock.timers.tick(300);
  assert.equal(fired, 3);
  repeater.release();
  ctx.mock.timers.tick(1_000);
  assert.equal(fired, 3);
  assert.equal(repeater.held, false);
  repeater.dispose();
  repeater.press();
  assert.equal(fired, 3);
});

test("repeat mode maps onto the manifest state order", async () => {
  const { repeatStateIndex } = await import("../src/model.ts");
  assert.equal(repeatStateIndex("off"), 0);
  assert.equal(repeatStateIndex("all"), 1);
  assert.equal(repeatStateIndex("one"), 2);
  assert.equal(repeatStateIndex("unknown"), 0);
  assert.equal(repeatStateIndex(undefined), 0);
});

test("playlist replies become dropdown items and junk is dropped", async () => {
  const { playlistItems } = await import("../src/keys.ts");
  assert.deepEqual(playlistItems([{ id: "DBID:1-IKIND:ePlaylist", name: "Jazz" }, { id: "", name: "x" }, null, { name: "no id" }]),
    [{ label: "Jazz", value: "DBID:1-IKIND:ePlaylist" }]);
  assert.deepEqual(playlistItems("nope"), []);
});

test("the playlist title auto-fills until the user touches it", async () => {
  const { playlistAutoTitle } = await import("../src/model.ts");
  assert.equal(playlistAutoTitle({ playlistId: "x", playlistName: "Jazz" }), "Jazz");
  assert.equal(playlistAutoTitle({ playlistId: "x", playlistName: "Jazz", titleTouched: true }), undefined);
  assert.equal(playlistAutoTitle({}), undefined);
});
