import assert from "node:assert/strict";
import test from "node:test";
import { adjustedVolume, formatTime, hasSelectedTrack, interpolatedPosition, marqueeText, volumeSettings, type BridgeState } from "../src/model.ts";
import { TickCoalescer } from "../src/tick-coalescer.ts";

test("per-action settings are clamped to supported ranges", () => {
  assert.deepEqual(volumeSettings({ volumeStepPercent: 0 }), { volumeStepPercent: 1 });
  assert.deepEqual(volumeSettings({}), { volumeStepPercent: 2 });
});

test("optimistic volume feedback follows the dial and stays bounded", () => {
  assert.equal(adjustedVolume(42, 4), 46);
  assert.equal(adjustedVolume(98, 5), 100);
  assert.equal(adjustedVolume(1, -5), 0);
  assert.equal(adjustedVolume(undefined, 2), undefined);
});

test("display time and progress interpolation stay bounded", () => {
  assert.equal(formatTime(65_000), "1:05");
  assert.equal(formatTime(undefined), "--:--");
  const state: BridgeState = {
    type: "state", revision: 1, timestampUtc: new Date().toISOString(),
    media: { available: true, playbackStatus: "playing", positionMs: 5_000, durationMs: 10_000 },
    audio: { available: false }
  };
  assert.equal(interpolatedPosition(state, 1_000, 20_000), 10_000);
});

test("a connected media session without a selected song stays visually neutral", () => {
  assert.equal(hasSelectedTrack({ available: true, title: "Apple Music", playbackStatus: "playing" }), false);
  assert.equal(hasSelectedTrack({ available: true, playbackStatus: "paused" }), false);
  assert.equal(hasSelectedTrack({ available: true, title: "Song title", playbackStatus: "paused" }), true);
  assert.equal(hasSelectedTrack({ available: true, durationMs: 180_000, playbackStatus: "paused" }), true);
});

test("rapid dial ticks are coalesced without losing direction", async () => {
  const flushed: number[] = [];
  const coalescer = new TickCoalescer(ticks => flushed.push(ticks), 5);
  coalescer.add(1);
  coalescer.add(2);
  coalescer.add(-1);
  await new Promise(resolve => setTimeout(resolve, 15));
  assert.deepEqual(flushed, [2]);
  coalescer.dispose();
});

test("a full track-control turn sends one track change", async () => {
  const commands: string[] = [];
  const coalescer = new TickCoalescer(ticks => commands.push(ticks > 0 ? "next" : "previous"), 5);
  for (let tick = 0; tick < 42; tick++) coalescer.add(1);
  await new Promise(resolve => setTimeout(resolve, 15));
  assert.deepEqual(commands, ["next"]);
  coalescer.dispose();
});

test("long touch-strip text pauses, scrolls, and wraps", () => {
  const text = "We Are Here For A Good Time";
  assert.equal(marqueeText(text, 10, 0), "We Are Her");
  assert.equal(marqueeText(text, 10, 1_750), "e Are Here");
  assert.equal(marqueeText("Short", 10, 99_000), "Short");
});
