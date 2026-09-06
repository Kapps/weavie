/** Reads newline-delimited JSON without buffering the complete response. */
export async function* readJsonStream<T>(response: Response): AsyncGenerator<T> {
  if (!response.ok) {
    throw new Error(`History request failed (HTTP ${response.status}).`);
  }
  if (response.body === null) {
    throw new Error("History response has no stream.");
  }
  const reader = response.body.getReader();
  const decoder = new TextDecoder("utf-8", { fatal: true });
  let parts: string[] = [];
  let finished = false;
  try {
    while (true) {
      const { value, done } = await reader.read();
      finished = done;
      const lines = decoder.decode(value, { stream: !done }).split("\n");
      for (const line of lines.slice(0, -1)) {
        parts.push(line);
        yield JSON.parse(parts.join("")) as T;
        parts = [];
      }
      parts.push(lines.at(-1)!);
      if (done) {
        if (parts.some((part) => part.length > 0)) {
          throw new Error("History response ended in an incomplete record.");
        }
        return;
      }
    }
  } finally {
    try {
      if (!finished) {
        await reader.cancel();
      }
    } finally {
      reader.releaseLock();
    }
  }
}
