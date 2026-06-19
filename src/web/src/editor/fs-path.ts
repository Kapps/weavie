// Windows reaches the same file through either drive-letter case (`C:\…` or `c:\…`), but the editor's
// `file://` scheme matches URIs case-SENSITIVELY here — the monaco-vscode-api overlay provider declares
// PathCaseSensitive, so `FileChangesEvent.contains` and URI identity fold no case. VSCode's own convention
// is a lowercase drive: `model.uri.fsPath` lowercases it (so does the C# LSP — `file:///c%3A/…`), and the
// persisted editor session round-trips through `fsPath`, so a restored working copy is opened as
// `file:///c%3A/…`. Host-supplied paths (Claude's edits, the workspace root) keep the OS's uppercase
// `C:\…`. Left unnormalized, a change event for `file:///C%3A/…` never matches the open `file:///c%3A/…`
// model, so the working copy never reloads — Claude's accepted edits stay invisible and an openDiff "keep"
// appears to revert to the original. Canonicalize every native path to a lowercase drive before turning it
// into a `file://` URI, so every URI we build for one on-disk file is byte-identical.
export function canonicalFsPath(path: string): string {
  return path.replace(/^([A-Za-z]):/, (_match, drive: string) => `${drive.toLowerCase()}:`);
}
