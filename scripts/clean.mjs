import { rm } from "node:fs/promises";

for (const path of [
  "com.davedev.clef.sdPlugin/bin",
  "com.davedev.clef.sdPlugin/helper",
  "com.davedev.clef.sdPlugin/LICENSE",
  "com.davedev.clef.sdPlugin/NOTICE",
  "com.davedev.clef.sdPlugin/THIRD_PARTY_NOTICES.md",
  "helper/ClefBridge/bin",
  "helper/ClefBridge/obj",
  "dist"
]) {
  await rm(path, { recursive: true, force: true });
}
