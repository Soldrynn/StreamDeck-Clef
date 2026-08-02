import assert from "node:assert/strict";
import test from "node:test";
import { LatestValuePump } from "../src/latest-value-pump.ts";

test("slow feedback retains only the latest pending update", async () => {
  const consumed: number[] = [];
  let releaseFirst: (() => void) | undefined;
  const firstBlocked = new Promise<void>(resolve => { releaseFirst = resolve; });
  const pump = new LatestValuePump<number>(async value => {
    consumed.push(value);
    if (value === 1) await firstBlocked;
  });

  pump.submit(1);
  pump.submit(2);
  pump.submit(3);
  await new Promise(resolve => setTimeout(resolve, 5));
  assert.deepEqual(consumed, [1]);

  releaseFirst?.();
  await new Promise(resolve => setTimeout(resolve, 5));
  assert.deepEqual(consumed, [1, 3]);
});

test("closing a feedback pump drops its queued value", async () => {
  const consumed: number[] = [];
  let releaseFirst: (() => void) | undefined;
  const firstBlocked = new Promise<void>(resolve => { releaseFirst = resolve; });
  const pump = new LatestValuePump<number>(async value => {
    consumed.push(value);
    if (value === 1) await firstBlocked;
  });

  pump.submit(1);
  pump.submit(2);
  pump.close();
  releaseFirst?.();
  await new Promise(resolve => setTimeout(resolve, 5));
  assert.deepEqual(consumed, [1]);
});
