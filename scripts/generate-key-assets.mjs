// Generates the Keypad action artwork from one shared glass base so every key
// matches the existing dial icons. Run: node scripts/generate-key-assets.mjs
import { mkdir, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const directory = join(root, "com.davedev.clef.sdPlugin", "assets", "actions");

const glass = `  <defs>
    <linearGradient id="face" x1="17" y1="12" x2="57" y2="62" gradientUnits="userSpaceOnUse">
      <stop stop-color="#4A4D51"/>
      <stop offset=".48" stop-color="#27292C"/>
      <stop offset="1" stop-color="#111214"/>
    </linearGradient>
    <linearGradient id="rim" x1="17" y1="9" x2="57" y2="64" gradientUnits="userSpaceOnUse">
      <stop stop-color="#FFFFFF" stop-opacity=".66"/>
      <stop offset=".4" stop-color="#FFFFFF" stop-opacity=".12"/>
      <stop offset="1" stop-color="#FFFFFF" stop-opacity=".34"/>
    </linearGradient>
    <radialGradient id="shine" cx="0" cy="0" r="1" gradientTransform="translate(29 20) rotate(56) scale(41)">
      <stop stop-color="#FFFFFF" stop-opacity=".18"/>
      <stop offset="1" stop-color="#FFFFFF" stop-opacity="0"/>
    </radialGradient>
  </defs>
  <circle cx="36" cy="37" r="33" fill="#08090A"/>
  <circle cx="36" cy="36" r="31" fill="url(#face)"/>
  <circle cx="36" cy="36" r="31" fill="url(#shine)"/>
  <circle cx="36" cy="36" r="29.5" fill="none" stroke="url(#rim)" stroke-width="1.2"/>
  <path d="M15 26c5-12 28-18 42-4" fill="none" stroke="#FFFFFF" stroke-width="1.2" stroke-linecap="round" opacity=".2"/>
  <circle cx="36" cy="36" r="16" fill="#FFFFFF" opacity=".045"/>`;

const stroke = `fill="none" stroke="#FFFFFF" stroke-width="2.8" stroke-linecap="round"`;
const speaker = `<path d="M19 31.5h7l9.5-7.5v24L26 40.5h-7Z" fill="#FFFFFF"/>`;

// Glyphs are drawn in the 72 × 72 key space; the list icons reuse them scaled.
const glyphs = {
  play: `<path d="M29.5 24.5 50 36 29.5 47.5Z" fill="#FFFFFF"/>`,
  pause: `<rect x="26" y="25" width="7" height="22" rx="1.5" fill="#FFFFFF"/><rect x="39" y="25" width="7" height="22" rx="1.5" fill="#FFFFFF"/>`,
  next: `<path d="M24 25.5 41 36 24 46.5Z" fill="#FFFFFF"/><rect x="43.5" y="25.5" width="4.5" height="21" rx="1.5" fill="#FFFFFF"/>`,
  previous: `<path d="M48 25.5 31 36 48 46.5Z" fill="#FFFFFF"/><rect x="24" y="25.5" width="4.5" height="21" rx="1.5" fill="#FFFFFF"/>`,
  "volume-up": `${speaker}<path d="M49 30v12M43 36h12" ${stroke}/>`,
  "volume-down": `${speaker}<path d="M43 36h12" ${stroke}/>`,
  unmuted: `${speaker}<path d="M41.5 29.7a9 9 0 0 1 0 12.6M47 24.5a16 16 0 0 1 0 23" ${stroke}/>`,
  muted: `${speaker}<path d="M43.5 31.5 52.5 40.5M52.5 31.5 43.5 40.5" ${stroke}/>`,
  "shuffle-off": `<g opacity=".5"><path d="M19 26h8l17 20h9M19 46h8l17-20h9" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/><path d="M49 21l5.5 5-5.5 5M49 41l5.5 5-5.5 5" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/></g>`,
  "shuffle-on": `<path d="M19 26h8l17 20h9M19 46h8l17-20h9" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/><path d="M49 21l5.5 5-5.5 5M49 41l5.5 5-5.5 5" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/><circle cx="36" cy="55" r="2.4" fill="#FFFFFF"/>`,
  "repeat-off": `<g opacity=".5"><path d="M22 34v-3a5 5 0 0 1 5-5h20M50 38v3a5 5 0 0 1-5 5H25" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/><path d="M42 21l5.5 5-5.5 5M30 41l-5.5 5 5.5 5" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/></g>`,
  "repeat-all": `<path d="M22 34v-3a5 5 0 0 1 5-5h20M50 38v3a5 5 0 0 1-5 5H25" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/><path d="M42 21l5.5 5-5.5 5M30 41l-5.5 5 5.5 5" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/><circle cx="36" cy="55" r="2.4" fill="#FFFFFF"/>`,
  "repeat-one": `<path d="M22 34v-3a5 5 0 0 1 5-5h20M50 38v3a5 5 0 0 1-5 5H25" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/><path d="M42 21l5.5 5-5.5 5M30 41l-5.5 5 5.5 5" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/><path d="M36.5 30.5v11M33.5 33l3-2.5" fill="none" stroke="#FFFFFF" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="36" cy="55" r="2.4" fill="#FFFFFF"/>`,
  favorite: `<path d="M36 21.5l4.3 9.2 10 1.2-7.4 6.9 2 9.9-8.9-5-8.9 5 2-9.9-7.4-6.9 10-1.2Z" fill="#FFFFFF"/>`,
  playlist: `<path d="M20 26h24M20 36h24M20 46h12" fill="none" stroke="#FFFFFF" stroke-width="2.8" stroke-linecap="round"/><path d="M38.5 40 49 46.5 38.5 53Z" fill="#FFFFFF"/>`,
  "now-playing": `<path d="M31.5 46V24.5l17-3.5v21" fill="none" stroke="#FFFFFF" stroke-width="2.6" stroke-linejoin="round" stroke-linecap="round"/><circle cx="26.7" cy="46.2" r="4.8" fill="#FFFFFF"/><circle cx="43.7" cy="42.2" r="4.8" fill="#FFFFFF"/>`
};

// List icons: the action-list glyph for each Keypad action (monochrome white).
const listIcons = {
  "play-pause": glyphs.play,
  next: glyphs.next,
  previous: glyphs.previous,
  "volume-up": glyphs["volume-up"],
  "volume-down": glyphs["volume-down"],
  mute: glyphs.unmuted,
  "now-playing": glyphs["now-playing"],
  shuffle: `<path d="M19 26h8l17 20h9M19 46h8l17-20h9" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/><path d="M49 21l5.5 5-5.5 5M49 41l5.5 5-5.5 5" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/>`,
  repeat: `<path d="M22 34v-3a5 5 0 0 1 5-5h20M50 38v3a5 5 0 0 1-5 5H25" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/><path d="M42 21l5.5 5-5.5 5M30 41l-5.5 5 5.5 5" fill="none" stroke="#FFFFFF" stroke-width="2.7" stroke-linecap="round" stroke-linejoin="round"/>`,
  favorite: glyphs.favorite,
  playlist: glyphs.playlist
};

function key(glyph, scale) {
  const size = 72 * scale;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 72 72">\n${glass}\n  ${glyph}\n</svg>\n`;
}

function list(glyph, scale) {
  const size = 20 * scale;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 20 20">
  <circle cx="10" cy="10" r="7.4" fill="none" stroke="#FFFFFF" stroke-width="1.4" opacity=".5"/>
  <g transform="translate(10 10) scale(0.3) translate(-36 -36)">${glyph}</g>
</svg>
`;
}

await mkdir(directory, { recursive: true });
const written = [];
for (const [name, glyph] of Object.entries(glyphs)) {
  for (const [suffix, scale] of [["", 1], ["@2x", 2]]) {
    const file = `key-${name}${suffix}.svg`;
    await writeFile(join(directory, file), key(glyph, scale));
    written.push(file);
  }
}
for (const [name, glyph] of Object.entries(listIcons)) {
  for (const [suffix, scale] of [["", 1], ["@2x", 2]]) {
    const file = `key-${name}-list${suffix}.svg`;
    await writeFile(join(directory, file), list(glyph, scale));
    written.push(file);
  }
}
console.log(`Wrote ${written.length} key assets.`);
