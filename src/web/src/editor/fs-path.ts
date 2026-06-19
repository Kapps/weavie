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

// A looser IDENTITY KEY for "do these two strings name the same file?" — distinct from canonicalFsPath, which
// produces a REAL, openable fs-path for building a file:// URI (it only lowercases the drive so the on-disk
// filename case and separators survive). normalizePath is NEVER used to open, read, display, or persist a file
// — only to compare — so it can be aggressive: it folds the WSL `/mnt/<drive>/` mount onto `<drive>:`, unifies
// `\`/`/` separators, drops a trailing slash, and lowercases the whole path. (Lowercasing could in theory
// conflate `Foo.cs` and `foo.cs` on a case-sensitive filesystem, but it only ever merges two tabs for
// same-named files — it never reaches disk — and Weavie's primary target is case-insensitive Windows.)
export function normalizePath(path: string): string {
  return (
    path
      .replace(/\\/g, "/")
      // WSL drive mount: `/mnt/c/Users/...` names the same file as `C:\Users\...` ⇒ fold to `c:/Users/...`. The
      // lookahead keeps a real directory like `/mnt/claude` (letters past the first) from being mistaken for one.
      .replace(/^\/mnt\/([a-zA-Z])(?=\/|$)/, (_match, drive: string) => `${drive}:`)
      // A file path never ends in a slash; folding `C:\foo\` onto `C:\foo` keeps trailing-slash variants matching.
      .replace(/\/+$/, "")
      .toLowerCase()
  );
}

/// True when both strings name the same on-disk file, ignoring drive-letter case, separator style, the WSL
/// `/mnt/<drive>/` mount prefix, a trailing slash, and (on case-insensitive Windows) filename case. Use this for
/// every "is this file already open?" comparison so one file maps to one tab however its path was spelled.
export function samePath(a: string, b: string): boolean {
  return normalizePath(a) === normalizePath(b);
}
