import { Emitter } from "@codingame/monaco-vscode-api/vscode/vs/base/common/event";
import type { IDisposable } from "@codingame/monaco-vscode-api/vscode/vs/base/common/lifecycle";
import type { URI } from "@codingame/monaco-vscode-api/vscode/vs/base/common/uri";
import {
  FileChangeType,
  FileSystemProviderCapabilities,
  FileSystemProviderError,
  FileSystemProviderErrorCode,
  FileType,
  type IFileChange,
  type IFileSystemProviderWithFileReadWriteCapability,
  type IStat,
  registerCustomProvider,
} from "@codingame/monaco-vscode-files-service-override";
import { type ClientSession, registerSessionFeature } from "../bridge";
import {
  SESSION_FILE_SCHEME,
  sessionFileUri,
  sessionForUri,
  sessionUriHostPath,
} from "./session-uri";

interface FileStat {
  exists: boolean;
  isDirectory: boolean;
  mtimeMs: number;
  ctimeMs: number;
  size: number;
}

interface FileReadResult {
  ok: boolean;
  content: string | null;
  stat: FileStat;
  code: string | null;
  error: string | null;
}

interface FileWriteResult {
  ok: boolean;
  stat: FileStat;
  error: string | null;
}

type FileChange = {
  path: string;
  kind: "updated" | "added" | "deleted";
};

const encoder = new TextEncoder();
const decoder = new TextDecoder();

function owner(resource: URI): ClientSession {
  const session = sessionForUri(resource);
  if (session === undefined) {
    throw FileSystemProviderError.create(
      `The session owning '${resource.toString()}' is no longer live.`,
      FileSystemProviderErrorCode.Unavailable,
    );
  }
  return session;
}

function mapChangeType(kind: FileChange["kind"]): FileChangeType {
  if (kind === "added") {
    return FileChangeType.ADDED;
  }
  return kind === "deleted" ? FileChangeType.DELETED : FileChangeType.UPDATED;
}

class HostFileProvider implements IFileSystemProviderWithFileReadWriteCapability {
  readonly capabilities =
    FileSystemProviderCapabilities.FileReadWrite | FileSystemProviderCapabilities.PathCaseSensitive;

  private readonly capabilitiesChanged = new Emitter<void>();
  readonly onDidChangeCapabilities = this.capabilitiesChanged.event;

  private readonly filesChanged = new Emitter<readonly IFileChange[]>();
  readonly onDidChangeFile = this.filesChanged.event;

  watch(): IDisposable {
    return { dispose: () => undefined };
  }

  async stat(resource: URI): Promise<IStat> {
    const result = await owner(resource)
      .feature("files")
      .request<FileStat, { path: string }>("stat", { path: sessionUriHostPath(resource) });
    if (!result.exists) {
      throw FileSystemProviderError.create(
        `Unable to resolve nonexistent file '${resource.toString()}'`,
        FileSystemProviderErrorCode.FileNotFound,
      );
    }
    return {
      type: result.isDirectory ? FileType.Directory : FileType.File,
      mtime: result.mtimeMs,
      ctime: result.ctimeMs,
      size: result.size,
    };
  }

  async readFile(resource: URI): Promise<Uint8Array> {
    const result = await owner(resource)
      .feature("files")
      .request<FileReadResult, { path: string }>("read", {
        path: sessionUriHostPath(resource),
      });
    if (result.ok && result.content !== null) {
      return encoder.encode(result.content);
    }
    if (result.code === "FileNotFound") {
      throw FileSystemProviderError.create(
        `Unable to resolve nonexistent file '${resource.toString()}'`,
        FileSystemProviderErrorCode.FileNotFound,
      );
    }
    throw FileSystemProviderError.create(
      result.error ?? `Unable to read file '${resource.toString()}'`,
      FileSystemProviderErrorCode.Unknown,
    );
  }

  async writeFile(resource: URI, content: Uint8Array): Promise<void> {
    const result = await owner(resource)
      .feature("files")
      .request<FileWriteResult, { path: string; content: string }>("write", {
        path: sessionUriHostPath(resource),
        content: decoder.decode(content),
      });
    if (!result.ok) {
      throw FileSystemProviderError.create(
        result.error ?? `Unable to write file '${resource.toString()}'`,
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

  fireChanges(session: ClientSession, changes: FileChange[]): void {
    const events = changes.map<IFileChange>((change) => ({
      type: mapChangeType(change.kind),
      resource: sessionFileUri(session, change.path),
    }));
    if (events.length > 0) {
      this.filesChanged.fire(events);
    }
  }

  private unsupported(operation: string): FileSystemProviderError {
    return FileSystemProviderError.create(
      `host file provider: ${operation} is not supported`,
      FileSystemProviderErrorCode.NoPermissions,
    );
  }
}

let installed = false;

export function installHostFileProvider(): void {
  if (installed) {
    return;
  }
  installed = true;
  const provider = new HostFileProvider();
  registerCustomProvider(SESSION_FILE_SCHEME, provider);
  registerSessionFeature((session) =>
    session
      .feature("files")
      .on<{ changes: FileChange[] }>("changed", ({ changes }) =>
        provider.fireChanges(session, changes),
      ),
  );
}
