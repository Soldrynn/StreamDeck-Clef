import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import sharp from "sharp";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const assets = join(root, "com.davedev.clef.sdPlugin", "assets");
const specifications = [
  ["plugin-icon.png", 256, 256],
  ["plugin-icon@2x.png", 512, 512],
  ["category.svg", 28, 28],
  ["category@2x.svg", 56, 56],
  ["actions/playback-list.svg", 20, 20],
  ["actions/playback-list@2x.svg", 40, 40],
  ["actions/volume-list.svg", 20, 20],
  ["actions/volume-list@2x.svg", 40, 40],
  ["actions/playback.svg", 72, 72],
  ["actions/playback@2x.svg", 144, 144],
  ["actions/volume.svg", 72, 72],
  ["actions/volume@2x.svg", 144, 144],
  ["placeholders/album.svg", 108, 108],
  ["placeholders/volume.svg", 108, 108],
  ["backgrounds/glass.svg", 200, 100],
  ["backgrounds/glass@2x.svg", 400, 200],
  ["actions/key-play.svg", 72, 72],
  ["actions/key-play@2x.svg", 144, 144],
  ["actions/key-pause.svg", 72, 72],
  ["actions/key-pause@2x.svg", 144, 144],
  ["actions/key-next.svg", 72, 72],
  ["actions/key-next@2x.svg", 144, 144],
  ["actions/key-previous.svg", 72, 72],
  ["actions/key-previous@2x.svg", 144, 144],
  ["actions/key-volume-up.svg", 72, 72],
  ["actions/key-volume-up@2x.svg", 144, 144],
  ["actions/key-volume-down.svg", 72, 72],
  ["actions/key-volume-down@2x.svg", 144, 144],
  ["actions/key-unmuted.svg", 72, 72],
  ["actions/key-unmuted@2x.svg", 144, 144],
  ["actions/key-muted.svg", 72, 72],
  ["actions/key-muted@2x.svg", 144, 144],
  ["actions/key-now-playing.svg", 72, 72],
  ["actions/key-now-playing@2x.svg", 144, 144],
  ["actions/key-play-pause-list.svg", 20, 20],
  ["actions/key-play-pause-list@2x.svg", 40, 40],
  ["actions/key-next-list.svg", 20, 20],
  ["actions/key-next-list@2x.svg", 40, 40],
  ["actions/key-previous-list.svg", 20, 20],
  ["actions/key-previous-list@2x.svg", 40, 40],
  ["actions/key-volume-up-list.svg", 20, 20],
  ["actions/key-volume-up-list@2x.svg", 40, 40],
  ["actions/key-volume-down-list.svg", 20, 20],
  ["actions/key-volume-down-list@2x.svg", 40, 40],
  ["actions/key-mute-list.svg", 20, 20],
  ["actions/key-mute-list@2x.svg", 40, 40],
  ["actions/key-now-playing-list.svg", 20, 20],
  ["actions/key-now-playing-list@2x.svg", 40, 40],
  ["actions/key-shuffle-off.svg", 72, 72],
  ["actions/key-shuffle-off@2x.svg", 144, 144],
  ["actions/key-shuffle-on.svg", 72, 72],
  ["actions/key-shuffle-on@2x.svg", 144, 144],
  ["actions/key-repeat-off.svg", 72, 72],
  ["actions/key-repeat-off@2x.svg", 144, 144],
  ["actions/key-repeat-all.svg", 72, 72],
  ["actions/key-repeat-all@2x.svg", 144, 144],
  ["actions/key-repeat-one.svg", 72, 72],
  ["actions/key-repeat-one@2x.svg", 144, 144],
  ["actions/key-favorite.svg", 72, 72],
  ["actions/key-favorite@2x.svg", 144, 144],
  ["actions/key-playlist.svg", 72, 72],
  ["actions/key-playlist@2x.svg", 144, 144],
  ["actions/key-shuffle-list.svg", 20, 20],
  ["actions/key-shuffle-list@2x.svg", 40, 40],
  ["actions/key-repeat-list.svg", 20, 20],
  ["actions/key-repeat-list@2x.svg", 40, 40],
  ["actions/key-favorite-list.svg", 20, 20],
  ["actions/key-favorite-list@2x.svg", 40, 40],
  ["actions/key-playlist-list.svg", 20, 20],
  ["actions/key-playlist-list@2x.svg", 40, 40]
];

for (const [relativePath, width, height] of specifications) {
  const path = join(assets, relativePath);
  const metadata = await sharp(path).metadata();
  assert.equal(metadata.width, width, `${relativePath} width`);
  assert.equal(metadata.height, height, `${relativePath} height`);
  await sharp(path).resize(width, height).png().toBuffer();
}

for (const relativePath of [
  "category.svg",
  "category@2x.svg",
  "actions/playback-list.svg",
  "actions/playback-list@2x.svg",
  "actions/volume-list.svg",
  "actions/volume-list@2x.svg",
  "actions/key-play-pause-list.svg",
  "actions/key-play-pause-list@2x.svg",
  "actions/key-next-list.svg",
  "actions/key-next-list@2x.svg",
  "actions/key-previous-list.svg",
  "actions/key-previous-list@2x.svg",
  "actions/key-volume-up-list.svg",
  "actions/key-volume-up-list@2x.svg",
  "actions/key-volume-down-list.svg",
  "actions/key-volume-down-list@2x.svg",
  "actions/key-mute-list.svg",
  "actions/key-mute-list@2x.svg",
  "actions/key-now-playing-list.svg",
  "actions/key-now-playing-list@2x.svg",
  "actions/key-shuffle-list.svg",
  "actions/key-shuffle-list@2x.svg",
  "actions/key-repeat-list.svg",
  "actions/key-repeat-list@2x.svg",
  "actions/key-favorite-list.svg",
  "actions/key-favorite-list@2x.svg",
  "actions/key-playlist-list.svg",
  "actions/key-playlist-list@2x.svg"
]) {
  const source = await readFile(join(assets, relativePath), "utf8");
  const colors = source.match(/#[0-9a-f]{6}/gi) ?? [];
  assert(colors.length > 0, `${relativePath} has a foreground`);
  assert(colors.every(color => color.toUpperCase() === "#FFFFFF"), `${relativePath} is monochrome white`);
}

for (const relativePath of ["backgrounds/glass.svg", "backgrounds/glass@2x.svg"]) {
  const source = await readFile(join(assets, relativePath), "utf8");
  assert.match(source, /fill-opacity="0"/, `${relativePath} remains transparent`);
}

console.log(`Validated and rendered ${specifications.length} visual assets.`);
