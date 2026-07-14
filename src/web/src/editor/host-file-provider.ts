// The host-backed `file://` provider through which VSCode working copies read/write real disk. Registered in
// front of monaco-vscode-api's empty in-memory overlay (priority 1), it proxies stat/read/write to the C# host
// over a correlated bridge and turns host `fs-change` pushes into the file service's change event so VSCode
// reloads affected copies. File-service substrate only, no UI. See docs/specs/file-management-and-sessions.md.

import { Emitter } from "@codingame/monaco-vscode-api/vscode/vs/base/common/event";
import type { IDisposable } from "@codingame/monaco-vscode-api/vscode/vs/base/common/lifecycle";
import { URI } from "@codingame/monaco-vscode-api/vscode/vs/base/common/uri";
import {
  FileChangeType,
  FileSystemProviderCapabilities,
  FileSystemProviderError,
  FileSystemProviderErrorCode,
  FileType,
  type IFileChange,
  type IFileSystemProviderWithFileReadWriteCapability,
  type IStat,
  registerFileSystemOverlay,
} from "@codingame/monaco-vscode-files-service-override";
import { onHostMessage, postToEditorBackend, type WebBoundMessage } from "../bridge";
import { canonicalFsPath, uriHostPath } from "./fs-path";

// A correlated request can't wedge a model resolve forever: if the host never replies (a dropped message, a
// host fault) the promise rejects after this, surfacing the failure rather than hanging the editor.
const REQUEST_TIMEOUT_MS = 10_000;

type FsResult =
  | Extract<WebBoundMessage, { type: "fs-stat-result" }>
  | Extract<WebBoundMessage, { type: "fs-read-result" }>
  | Extract<WebBoundMessage, { type: "fs-write-result" }>;

const encoder = new TextEncoder();
const decoder = new TextDecoder();

let seq = 0;
const pending = new Map<string, (result: FsResult) => void>();

/** Sends one correlated fs request and resolves with the matching host reply (rejects on timeout). */
function request(message: {
  type: "fs-stat" | "fs-read" | "fs-write";
  path: string;
  content?: string;
}): Promise<FsResult> {
  const id = `fs${++seq}`;
  return new Promise<FsResult>((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(
        new Error(
          `file provider: ${message.type} ${message.path} timed out after ${REQUEST_TIMEOUT_MS}ms`,
        ),
      );
    }, REQUEST_TIMEOUT_MS);
    pending.set(id, (result) => {
      clearTimeout(timer);
      pending.delete(id);
      resolve(result);
    });
    if (message.type === "fs-write") {
      postToEditorBackend({
        type: "fs-write",
        id,
        path: message.path,
        content: message.content ?? "",
      });
    } else {
      postToEditorBackend({ type: message.type, id, path: message.path });
    }
  });
}

function mapChangeType(kind: "updated" | "added" | "deleted"): FileChangeType {
  if (kind === "added") {
    return FileChangeType.ADDED;
  }
  if (kind === "deleted") {
    return FileChangeType.DELETED;
  }
  return FileChangeType.UPDATED;
}

/**
 * A `file://` provider backed by the C# host: read/write capable and path-case-sensitive, so its working
 * copies are editable. Directory ops throw coded errors — the editor only resolves file copies, never dirs.
 */
class HostFileProvider implements IFileSystemProviderWithFileReadWriteCapability {
  readonly capabilities =
    FileSystemProviderCapabilities.FileReadWrite | FileSystemProviderCapabilities.PathCaseSensitive;

  private readonly _onDidChangeCapabilities = new Emitter<void>();
  readonly onDidChangeCapabilities = this._onDidChangeCapabilities.event;

  private readonly _onDidChangeFile = new Emitter<readonly IFileChange[]>();
  readonly onDidChangeFile = this._onDidChangeFile.event;

  // No per-resource watching; the host pushes fs-change for the whole workspace. VSCode still calls watch()
  // for every resolved model, so make it a cheap no-op.
  watch(): IDisposable {
    return { dispose: () => undefined };
  }

  async stat(resource: URI): Promise<IStat> {
    // uriHostPath, never resource.fsPath: fsPath renders in the browser's OS convention, which mangles the
    // remote host's path when the two differ (a Windows browser backslashes a Linux host's path).
    const result = await request({ type: "fs-stat", path: uriHostPath(resource) });
    if (result.type !== "fs-stat-result" || !result.ok || !result.exists) {
      throw FileSystemProviderError.create(
        `Unable to resolve nonexistent file '${resource.toString()}'`,
        FileSystemProviderErrorCode.FileNotFound,
      );
    }
    return {
      type: result.isDir ? FileType.Directory : FileType.File,
      mtime: result.mtimeMs,
      ctime: result.ctimeMs,
      size: result.size,
    };
  }

  async readFile(resource: URI): Promise<Uint8Array> {
    const result = await request({ type: "fs-read", path: uriHostPath(resource) });
    if (result.type === "fs-read-result" && result.ok && result.content !== undefined) {
      return encoder.encode(result.content);
    }
    // FileNotFound is a coded error the overlay catches and falls through to the empty in-memory layer, so a
    // genuinely missing file reads as missing instead of erroring.
    if (result.type === "fs-read-result" && result.code === "FileNotFound") {
      throw FileSystemProviderError.create(
        `Unable to resolve nonexistent file '${resource.toString()}'`,
        FileSystemProviderErrorCode.FileNotFound,
      );
    }
    // A real read failure on an existing file is Unknown — not in the overlay's fall-through set, so it
    // propagates rather than being swallowed.
    const error = result.type === "fs-read-result" ? result.error : undefined;
    throw FileSystemProviderError.create(
      error ?? `Unable to read file '${resource.toString()}'`,
      FileSystemProviderErrorCode.Unknown,
    );
  }

  async writeFile(resource: URI, content: Uint8Array): Promise<void> {
    const result = await request({
      type: "fs-write",
      path: uriHostPath(resource),
      content: decoder.decode(content),
    });
    if (result.type !== "fs-write-result" || !result.ok) {
      const error = result.type === "fs-write-result" ? result.error : undefined;
      throw FileSystemProviderError.create(
        error ?? `Unable to write file '${resource.toString()}'`,
        FileSystemProviderErrorCode.Unknown,
      );
    }
  }

  mkdir(): Promise<void> {
    return Promise.reject(this.unsupported("mkdir"));
  }

  readdir(): Promise<[string, FileType][]> {
    return Promise.reject(this.unsupported("readdir"));
  }

  delete(): Promise<void> {
    return Promise.reject(this.unsupported("delete"));
  }

  rename(): Promise<void> {
    return Promise.reject(this.unsupported("rename"));
  }

  /** Turns a host fs-change batch into the file service's change event so VSCode reloads non-dirty copies. */
  fireChanges(changes: { path: string; kind: "updated" | "added" | "deleted" }[]): void {
    const events: IFileChange[] = changes.map((change) => ({
      type: mapChangeType(change.kind),
      // Drive-case canonicalization (see canonicalFsPath): the host sends `C:\…`, the open working copy is
      // `file:///c%3A/…`, and `file://` matching is case-sensitive, so without this the change never matches.
      resource: URI.file(canonicalFsPath(change.path)),
    }));
    if (events.length > 0) {
      this._onDidChangeFile.fire(events);
    }
  }

  private unsupported(op: string): FileSystemProviderError {
    // NoPermissions is in the overlay's fall-through set, so an accidental dir op degrades rather than
    // crashing; the editor never asks the file:// provider to mutate directories.
    return FileSystemProviderError.create(
      `host file provider: ${op} is not supported`,
      FileSystemProviderErrorCode.NoPermissions,
    );
  }
}

let installed = false;

/**
 * Registers the provider in front of the empty in-memory layer and wires the bridge client. Must run after
 * `initialize()` (the file service must exist). Idempotent.
 */
export function installHostFileProvider(): void {
  if (installed) {
    return;
  }
  installed = true;

  const provider = new HostFileProvider();
  // Priority 1 > the default empty in-memory layer at priority 0, so this provider answers first and the
  // empty layer remains only as the not-found fall-through.
  registerFileSystemOverlay(1, provider);

  onHostMessage((message) => {
    if (
      message.type === "fs-stat-result" ||
      message.type === "fs-read-result" ||
      message.type === "fs-write-result"
    ) {
      pending.get(message.id)?.(message);
    } else if (message.type === "fs-change") {
      provider.fireChanges(message.changes);
    }
  });
}
