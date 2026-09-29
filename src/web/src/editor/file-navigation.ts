import type { ClientSession } from "../bridge";
import type { FocusIntent } from "../chrome/interaction-intent";
import type { FileNavigation, FileReferenceResolution } from "../files/reveal";

export interface ResolvedFileOpen {
  path: string;
  line: number | null;
  preview: boolean;
}

export interface FileOpenCommit {
  files: { path: string; preview: boolean }[];
  activePath: string | null;
  originPageEpoch: string;
}

/** Reference navigation is cancellable; delivered file opens are durable session operations. */
export function createFileNavigation(
  session: ClientSession,
  options: {
    beginFocus(): FocusIntent | undefined;
    navigationSignal(): AbortSignal;
    commit(
      files: ResolvedFileOpen[],
      activePath: string | null,
      focus: FocusIntent | undefined,
    ): Promise<void>;
    ambiguous(query: string, line: number | null): void;
    error(message: string): void;
  },
): FileNavigation & { dispose(): void } {
  const lifetime = new AbortController();
  const signal = AbortSignal.any([session.signal, lifetime.signal]);
  let destination: AbortController | undefined;
  const begin = () => {
    destination?.abort();
    destination = new AbortController();
    return {
      validity: AbortSignal.any([signal, destination.signal, options.navigationSignal()]),
      focus: options.beginFocus(),
    };
  };
  const resolve = (path: string, line: number | undefined, signal: AbortSignal) =>
    session
      .feature("files")
      .request<FileReferenceResolution, { path: string; line: number | null }>(
        "resolveReference",
        { path, line: line ?? null },
        signal,
      );
  const report = (error: unknown): void =>
    options.error(`Couldn't open the file: ${String(error)}`);
  const reportAmbiguous = (query: string): void =>
    options.error(`Several files match ${query}. Choose one with Go to File.`);
  return {
    async reveal(path, line, preview) {
      const { validity, focus } = begin();
      try {
        const result = await resolve(path, line, validity);
        if (validity.aborted) return;
        if (result.kind === "missing") options.error(result.message);
        else if (focus === undefined || focus.current()) {
          if (result.kind === "ambiguous") {
            if (focus?.current()) options.ambiguous(result.query, result.line);
          } else {
            return options
              .commit([{ ...result, preview }], result.path, focus)
              .catch((error: unknown) => {
                if (!signal.aborted) report(error);
              });
          }
        }
      } catch (error) {
        if (!validity.aborted) report(error);
      }
    },
    async openFiles(paths) {
      const { validity, focus } = begin();
      try {
        const results = await Promise.allSettled(
          paths.map((path) => resolve(path, undefined, signal)),
        );
        if (signal.aborted) return;
        const files: ResolvedFileOpen[] = [];
        let ambiguous: Extract<FileReferenceResolution, { kind: "ambiguous" }> | undefined;
        for (const result of results) {
          if (result.status === "rejected") report(result.reason);
          else if (result.value.kind === "file") files.push({ ...result.value, preview: false });
          else if (result.value.kind === "missing") options.error(result.value.message);
          else {
            if (ambiguous !== undefined) reportAmbiguous(ambiguous.query);
            ambiguous = result.value;
          }
        }
        const current = !validity.aborted && (focus === undefined || focus.current());
        const committing =
          files.length === 0
            ? Promise.resolve()
            : options.commit(
                files,
                current && ambiguous === undefined ? files.at(-1)!.path : null,
                focus,
              );
        if (ambiguous !== undefined) {
          if (current && focus?.current()) options.ambiguous(ambiguous.query, ambiguous.line);
          else reportAmbiguous(ambiguous.query);
        }
        await committing;
      } catch (error) {
        if (!signal.aborted) report(error);
      }
    },
    dispose: () => lifetime.abort(),
  };
}
