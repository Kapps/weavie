interface WireChunk {
  id: string;
  index: number;
  count: number;
  data: string;
}

interface PendingMessage {
  id: string;
  count: number;
  chunks: Uint8Array[];
  bytes: number;
}

export class ChunkedMessageReceiver {
  private pending: PendingMessage | null = null;

  ingest(raw: string): string | null {
    const chunk = parseChunk(raw);
    if (chunk === null) {
      return raw;
    }

    if (chunk.index === 0 && this.pending === null) {
      this.pending = { id: chunk.id, count: chunk.count, chunks: [], bytes: 0 };
    }
    const pending = this.pending;
    if (
      pending === null ||
      pending.id !== chunk.id ||
      pending.count !== chunk.count ||
      chunk.index !== pending.chunks.length
    ) {
      throw new Error("Received an out-of-order Weavie message chunk.");
    }

    const bytes = decodeBase64(chunk.data);
    pending.chunks.push(bytes);
    pending.bytes += bytes.length;
    if (pending.chunks.length !== pending.count) {
      return null;
    }

    const joined = new Uint8Array(pending.bytes);
    let offset = 0;
    for (const part of pending.chunks) {
      joined.set(part, offset);
      offset += part.length;
    }
    this.pending = null;
    return new TextDecoder("utf-8", { fatal: true }).decode(joined);
  }

  reset(): void {
    this.pending = null;
  }
}

function parseChunk(raw: string): WireChunk | null {
  if (!raw.startsWith('{"$weavieChunk":')) {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }
  if (typeof parsed !== "object" || parsed === null || !("$weavieChunk" in parsed)) {
    return null;
  }
  const chunk = (parsed as { $weavieChunk?: unknown }).$weavieChunk;
  if (typeof chunk !== "object" || chunk === null) {
    throw new Error("Received an invalid Weavie message chunk.");
  }
  const candidate = chunk as Partial<WireChunk>;
  if (
    typeof candidate.id !== "string" ||
    candidate.id.length === 0 ||
    !Number.isInteger(candidate.index) ||
    (candidate.index ?? -1) < 0 ||
    !Number.isInteger(candidate.count) ||
    (candidate.count ?? 0) <= 0 ||
    (candidate.index ?? 0) >= (candidate.count ?? 0) ||
    typeof candidate.data !== "string"
  ) {
    throw new Error("Received an invalid Weavie message chunk.");
  }
  return candidate as WireChunk;
}

function decodeBase64(value: string): Uint8Array {
  const decoded = atob(value);
  return Uint8Array.from(decoded, (character) => character.charCodeAt(0));
}
