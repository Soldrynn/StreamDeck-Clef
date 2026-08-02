import { rm } from "node:fs/promises";

for (const path of [
  "com.davedev.apple-music.sdPlugin/bin",
  "com.davedev.apple-music.sdPlugin/helper",
  "com.davedev.apple-music.sdPlugin/LICENSE",
  "com.davedev.apple-music.sdPlugin/NOTICE",
  "com.davedev.apple-music.sdPlugin/THIRD_PARTY_NOTICES.md",
  "helper/AppleMusicBridge/bin",
  "helper/AppleMusicBridge/obj",
  "dist"
]) {
  await rm(path, { recursive: true, force: true });
}
