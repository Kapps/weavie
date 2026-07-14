// Fetches a media file's bytes from the host (fs-stat size gate → fs-read-bytes → Blob URL) and tracks each
// open media path's render state, keyed by the tab path. Own id namespace (`media{n}`) so replies never
// collide with host-file-provider's `fs{n}` correlation map. A host fs-change push for a tracked path
// re-fetches it (the old frame stays up until the new bytes land); a delete becomes a loud in-pane error.
import { createSignal } from "solid-js";
import { onHostMessage, postToEditorBackend } from "../../bridge";
import { basename, samePath } from "../fs-path";
import { MAX_MEDIA_BYTES, mediaTypeOf } from "./media-types";

// The pane surfaces a dropped/hung request as an in-pane error instead of an eternal spinner. Wider than the
// file provider's 10s: a bound-sized video over a remote WSS link is a legitimately slow single message.
const REQUEST_TIMEOUT_MS = 30_000;

export interface MediaEntry {
  status: "loading" | "ready" | "error";
  // The Blob URL to render once bytes have arrived; during a re-fetch it stays set so the old frame holds.
  url?: string | undefined;
  // Set when status is "error": the failure reason, rendered verbatim in the pane.
  error?: string;
}

const [entries, setEntries] = createSignal<Record<string, MediaEntry>>({});
let seq = 0;
// In-flight correlation: request id → path, and path → its CURRENT request id (a reply whose id is no longer
// current — superseded by a re-fetch or released — is dropped without touching state).
const requestPath = new Map<string, string>();
const currentId = new Map<string, string>();
let wired = false;

/** The render state for `path`, or undefined before {@link loadMedia} has been asked for it. */
export function mediaDoc(path: string): MediaEntry | undefined {
  return entries()[path];
}

/** Starts fetching `path`'s bytes unless already loading/loaded. The pane calls this on mount/path change. */
export function loadMedia(path: string): void {
  ensureWired();
  if (entries()[path] === undefined) {
    fetchMedia(path);
  }
}

/** Drops `path`'s state and revokes its Blob URL; any in-flight reply for it is discarded. */
export function releaseMedia(path: string): void {
  const url = entries()[path]?.url;
  if (url !== undefined) {
    URL.revokeObjectURL(url);
  }
  currentId.delete(path);
  setEntries((prev) => {
    const { [path]: _dropped, ...rest } = prev;
    return rest;
  });
}

function setEntry(path: string, entry: MediaEntry): void {
  setEntries((prev) => ({ ...prev, [path]: entry }));
}

function fetchMedia(path: string): void {
  setEntry(path, { status: "loading", url: entries()[path]?.url });
  send(path, "fs-stat");
}

function send(path: string, type: "fs-stat" | "fs-read-bytes"): void {
  const id = `media${++seq}`;
  requestPath.set(id, path);
  currentId.set(path, id);
  setTimeout(() => {
    if (currentId.get(path) === id) {
      fail(path, `Timed out reading ${basename(path)} from the host.`);
    }
    requestPath.delete(id);
  }, REQUEST_TIMEOUT_MS);
  postToEditorBackend({ type, id, path });
}

function fail(path: string, error: string): void {
  currentId.delete(path);
  const url = entries()[path]?.url;
  if (url !== undefined) {
    URL.revokeObjectURL(url);
  }
  setEntry(path, { status: "error", error });
}

// Resolves a reply's path when it is the CURRENT request for that path; stale/foreign ids resolve to null.
function claim(id: string): string | null {
  const path = requestPath.get(id);
  if (path === undefined) {
    return null;
  }
  requestPath.delete(id);
  return currentId.get(path) === id ? path : null;
}

function onStat(
  id: string,
  ok: boolean,
  exists: boolean,
  size: number,
  error: string | undefined,
): void {
  const path = claim(id);
  if (path === null) {
    return;
  }
  if (!ok) {
    fail(path, error ?? `Unable to stat ${basename(path)}.`);
  } else if (!exists) {
    fail(path, `${basename(path)} was not found.`);
  } else if (size > MAX_MEDIA_BYTES) {
    const mb = (bytes: number): string => `${Math.round(bytes / (1024 * 1024))} MB`;
    fail(
      path,
      `${basename(path)} is ${mb(size)} — too large to load over the Weavie bridge (limit ${mb(MAX_MEDIA_BYTES)}).`,
    );
  } else {
    send(path, "fs-read-bytes");
  }
}

function onBytes(id: string, dataB64: string | undefined, error: string | undefined): void {
  const path = claim(id);
  if (path === null) {
    return;
  }
  if (dataB64 === undefined) {
    fail(path, error ?? `Unable to read ${basename(path)}.`);
    return;
  }
  const binary = atob(dataB64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  const previous = entries()[path]?.url;
  if (previous !== undefined) {
    URL.revokeObjectURL(previous);
  }
  const mime = mediaTypeOf(path)?.mime ?? "";
  currentId.delete(path);
  setEntry(path, { status: "ready", url: URL.createObjectURL(new Blob([bytes], { type: mime })) });
}

function ensureWired(): void {
  if (wired) {
    return;
  }
  wired = true;
  onHostMessage((message) => {
    if (message.type === "fs-stat-result" && requestPath.has(message.id)) {
      onStat(message.id, message.ok, message.exists, message.size, message.error);
    } else if (message.type === "fs-read-bytes-result" && requestPath.has(message.id)) {
      onBytes(message.id, message.ok ? message.dataB64 : undefined, message.error ?? message.code);
    } else if (message.type === "fs-change") {
      for (const change of message.changes) {
        const tracked = Object.keys(entries()).find((path) => samePath(path, change.path));
        if (tracked === undefined) {
          continue;
        }
        if (change.kind === "deleted") {
          fail(tracked, `${basename(tracked)} was deleted.`);
        } else {
          fetchMedia(tracked);
        }
      }
    }
  });
}
