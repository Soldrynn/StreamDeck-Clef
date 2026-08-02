export class TickCoalescer {
  #ticks = 0;
  #timer?: NodeJS.Timeout;
  readonly #flushCallback: (ticks: number) => void;
  readonly #delayMs: number;

  constructor(flushCallback: (ticks: number) => void, delayMs = 28) {
    this.#flushCallback = flushCallback;
    this.#delayMs = delayMs;
  }

  add(ticks: number): void {
    if (!Number.isFinite(ticks) || ticks === 0) return;
    this.#ticks += Math.trunc(ticks);
    if (this.#timer) clearTimeout(this.#timer);
    this.#timer = setTimeout(() => this.flush(), this.#delayMs);
  }

  flush(): void {
    if (this.#timer) clearTimeout(this.#timer);
    this.#timer = undefined;
    const ticks = this.#ticks;
    this.#ticks = 0;
    if (ticks !== 0) this.#flushCallback(ticks);
  }

  dispose(): void {
    if (this.#timer) clearTimeout(this.#timer);
    this.#timer = undefined;
    this.#ticks = 0;
  }
}
