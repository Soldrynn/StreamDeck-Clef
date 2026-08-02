export class LatestValuePump<T> {
  #pending?: T;
  #running = false;
  #closed = false;
  readonly consume: (value: T) => Promise<void>;
  readonly onError?: (error: unknown) => void;

  constructor(
    consume: (value: T) => Promise<void>,
    onError?: (error: unknown) => void
  ) {
    this.consume = consume;
    this.onError = onError;
  }

  submit(value: T): void {
    if (this.#closed) return;
    this.#pending = value;
    if (this.#running) return;
    this.#running = true;
    void this.#drain();
  }

  close(): void {
    this.#closed = true;
    this.#pending = undefined;
  }

  async #drain(): Promise<void> {
    try {
      while (!this.#closed && this.#pending !== undefined) {
        const value = this.#pending;
        this.#pending = undefined;
        try {
          await this.consume(value);
        } catch (error) {
          this.onError?.(error);
        }
      }
    } finally {
      this.#running = false;
      if (!this.#closed && this.#pending !== undefined) {
        this.#running = true;
        void this.#drain();
      }
    }
  }
}
