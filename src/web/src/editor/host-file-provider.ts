// The host-backed `file://` provider: the bridge through which Monaco's VSCode working copies read and write
// the REAL disk. monaco-vscode-api's file service backs `file://` with an OverlayFileSystemProvider whose only
// layer is an empty in-memory FS, so resolving a `file://` model reference (createModelReference →
// TextFileEditorModel.resolve → fileService.readFile) used to fail with "Unable to read file …" and break
// every feature that resolves one (occurrence highlighting, peek/references, format, …). This module fills
// that gap: a provider registered in FRONT of the empty layer (priority 1) that proxies stat/read/write to
// the C# host (→ real disk) over a correlated bridge, and turns host `fs-change` pushes into the file
// service's change event so VSCode reloads the affected working copies. No workbench/UI is involved — this is
// purely the file-service substrate. See docs/specs/file-management-and-sessions.md and the plan in
// memory file-scheme-empty-provider.md.

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
import { type WebBoundMessage, onHostMessage, postToHost } from "../bridge";

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
      postToHost({ type: "fs-write", id, path: message.path, content: message.content ?? "" });
    } else {
      postToHost({ type: message.type, id, path: message.path });
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
 * A `file://` provider backed by the C# host. Read/write capable (NOT readonly) and path-case-sensitive, so
 * its working copies are editable. Directory ops throw coded errors — the editor only ever resolves file
 * working copies, and the file service tolerates these (it stats parents before writing, never lists dirs).
 */
class HostFileProvider implements IFileSystemProviderWithFileReadWriteCapability {
  readonly capabilities =
    FileSystemProviderCapabilities.FileReadWrite | FileSystemProviderCapabilities.PathCaseSensitive;

  private readonly _onDidChangeCapabilities = new Emitter<void>();
  readonly onDidChangeCapabilities = this._onDidChangeCapabilities.event;

  private readonly _onDidChangeFile = new Emitter<readonly IFileChange[]>();
  readonly onDidChangeFile = this._onDidChangeFile.event;

  // We don't watch per-resource; the host pushes fs-change for the whole workspace (Claude edits + the
  // workspace watcher). VSCode still calls watch() for every resolved model — make it a cheap no-op.
  watch(): IDisposable {
    return { dispose: () => undefined };
  }

  async stat(resource: URI): Promise<IStat> {
    const result = await request({ type: "fs-stat", path: resource.fsPath });
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
    const result = await request({ type: "fs-read", path: resource.fsPath });
    if (result.type === "fs-read-result" && result.ok && result.content !== undefined) {
      return encoder.encode(result.content);
    }
    // FileNotFound is a coded error the overlay catches → it falls through to the empty in-memory layer
    // (which also reports not-found), so a genuinely missing file reads as missing instead of erroring.
    if (result.type === "fs-read-result" && result.code === "FileNotFound") {
      throw FileSystemProviderError.create(
        `Unable to resolve nonexistent file '${resource.toString()}'`,
        FileSystemProviderErrorCode.FileNotFound,
      );
    }
    // A real read failure on an existing file is Unknown — NOT in the overlay's fall-through set, so it
    // propagates loudly rather than being silently swallowed.
    const error = result.type === "fs-read-result" ? result.error : undefined;
    throw FileSystemProviderError.create(
      error ?? `Unable to read file '${resource.toString()}'`,
      FileSystemProviderErrorCode.Unknown,
    );
  }

  async writeFile(resource: URI, content: Uint8Array): Promise<void> {
    const result = await request({
      type: "fs-write",
      path: resource.fsPath,
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
      resource: URI.file(change.path),
    }));
    if (events.length > 0) {
      this._onDidChangeFile.fire(events);
    }
  }

  private unsupported(op: string): FileSystemProviderError {
    // NoPermissions is in the overlay's fall-through set, so an accidental dir op degrades to "no delegate
    // handled it" rather than crashing — the editor never asks the file:// provider to mutate directories.
    return FileSystemProviderError.create(
      `host file provider: ${op} is not supported`,
      FileSystemProviderErrorCode.NoPermissions,
    );
  }
}

let installed = false;

/**
 * Registers the host-backed `file://` provider in front of the empty in-memory layer and wires the correlated
 * bridge client. Must run AFTER `initialize()` (the file service must exist). Idempotent — a second call (e.g.
 * a dev hot reload that re-imports this module) is a no-op, so the overlay + listener are installed once.
 */
export function installHostFileProvider(): void {
  if (installed) {
    return;
  }
  installed = true;

  const provider = new HostFileProvider();
  // Priority 1 > the default empty in-memory layer at priority 0, so our provider answers first and the
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
