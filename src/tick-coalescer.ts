export class TickCoalescer {
  #ticks = 0;
  #timer?: NodeJS.Timeout;
  #deadline = 0;
  readonly #flushCallback: (ticks: number) => void;
  readonly #delayMs: number;
  readonly #maxWaitMs: number;

  constructor(flushCallback: (ticks: number) => void, delayMs = 28, maxWaitMs = Number.POSITIVE_INFINITY) {
    this.#flushCallback = flushCallback;
    this.#delayMs = delayMs;
    this.#maxWaitMs = maxWaitMs;
  }

  add(ticks: number): void {
    if (!Number.isFinite(ticks) || ticks === 0) return;
    const now = Date.now();
    if (this.#deadline === 0) this.#deadline = now + this.#maxWaitMs;
    this.#ticks += Math.trunc(ticks);
    if (this.#timer) clearTimeout(this.#timer);
    this.#timer = setTimeout(() => this.flush(), Math.max(0, Math.min(this.#delayMs, this.#deadline - now)));
  }

  flush(): void {
    if (this.#timer) clearTimeout(this.#timer);
    this.#timer = undefined;
    this.#deadline = 0;
    const ticks = this.#ticks;
    this.#ticks = 0;
    if (ticks !== 0) this.#flushCallback(ticks);
  }

  dispose(): void {
    if (this.#timer) clearTimeout(this.#timer);
    this.#timer = undefined;
    this.#deadline = 0;
    this.#ticks = 0;
  }
}
