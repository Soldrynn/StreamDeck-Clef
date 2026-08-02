import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";

interface LayoutItem {
  key: string;
  rect: number[];
  color?: string;
  font?: object;
  bar_bg_c?: string;
  bar_fill_c?: string;
  bar_border_c?: string;
  border_w?: number;
  subtype?: number;
}

interface Layout { items: LayoutItem[] }

const layoutsDirectory = join(process.cwd(), "com.davedev.apple-music.sdPlugin", "layouts");
const playback = JSON.parse(readFileSync(join(layoutsDirectory, "playback.json"), "utf8")) as Layout;
const volume = JSON.parse(readFileSync(join(layoutsDirectory, "volume.json"), "utf8")) as Layout;

function item(layout: Layout, key: string): LayoutItem {
  const result = layout.items.find(candidate => candidate.key === key);
  assert.ok(result, `Missing layout item: ${key}`);
  return result;
}

test("playback and volume layouts share one visual grid", () => {
  assert.deepEqual(item(volume, "glyph").rect, item(playback, "art").rect);
  assert.deepEqual(item(volume, "status").rect, item(playback, "status").rect);
  assert.deepEqual(item(volume, "value").rect, item(playback, "track").rect);
  assert.deepEqual(item(volume, "detail").rect, item(playback, "artist").rect);

  const playbackBar = item(playback, "progress");
  const volumeBar = item(volume, "volume");
  for (const property of ["rect", "bar_bg_c", "bar_fill_c", "bar_border_c", "border_w", "subtype"] as const) {
    assert.deepEqual(volumeBar[property], playbackBar[property]);
  }
});
