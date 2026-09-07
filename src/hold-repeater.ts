/**
 * Repeats an action while a key is held. Stream Deck sends a single keyDown and
 * keyUp, so held volume keys need their own auto-repeat.
 */
export class HoldRepeater {
  #timer?: NodeJS.Timeout;
  #disposed = false;
  readonly #fire: () => void;
  readonly #initialDelayMs: number;
  readonly #intervalMs: number;

  readonly #maxHoldMs: number;
  #startedAt = 0;

  constructor(fire: () => void, initialDelayMs = 450, intervalMs = 150, maxHoldMs = 8_000) {
    this.#fire = fire;
    this.#initialDelayMs = initialDelayMs;
    this.#intervalMs = intervalMs;
    this.#maxHoldMs = maxHoldMs;
  }

  press(): void {
    if (this.#disposed) return;
    this.release();
    this.#startedAt = Date.now();
    this.#fire();
    this.#timer = setTimeout(() => {
      this.#timer = setInterval(() => {
        if (Date.now() - this.#startedAt > this.#maxHoldMs) {
          this.release();
          return;
        }
        this.#fire();
      }, this.#intervalMs);
    }, this.#initialDelayMs);
  }

  release(): void {
    if (this.#timer) {
      clearTimeout(this.#timer);
      clearInterval(this.#timer);
      this.#timer = undefined;
    }
  }

  get held(): boolean { return this.#timer !== undefined; }

  dispose(): void {
    this.release();
    this.#disposed = true;
  }
}
