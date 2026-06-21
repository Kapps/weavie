// Canonicalize a native path to a lowercase drive letter before building a `file://` URI. The editor's
// file:// scheme matches URIs case-sensitively (the monaco-vscode-api overlay declares PathCaseSensitive),
// and VSCode's convention is a lowercase drive (`model.uri.fsPath`), so without this a change event for
// `file:///C%3A/…` would never match an open `file:///c%3A/…` model and the working copy would never reload.
export function canonicalFsPath(path: string): string {
  return path.replace(/^([A-Za-z]):/, (_match, drive: string) => `${drive.toLowerCase()}:`);
}

// A looser identity key for "do these two strings name the same file?", for comparison only — never to open,
// read, display, or persist. Folds the WSL `/mnt/<drive>/` mount onto `<drive>:`, unifies separators, drops a
// trailing slash, and lowercases the whole path (safe since it only ever merges same-named tabs, never disk).
export function normalizePath(path: string): string {
  return (
    path
      .replace(/\\/g, "/")
      // WSL drive mount: `/mnt/c/Users/...` ⇒ `c:/Users/...`. The lookahead keeps a real directory like
      // `/mnt/claude` (letters past the first) from being mistaken for a drive mount.
      .replace(/^\/mnt\/([a-zA-Z])(?=\/|$)/, (_match, drive: string) => `${drive}:`)
      // A file path never ends in a slash; fold `C:\foo\` onto `C:\foo`.
      .replace(/\/+$/, "")
      .toLowerCase()
  );
}

/// True when both strings name the same on-disk file, ignoring drive-letter case, separator style, the WSL
/// `/mnt/<drive>/` mount prefix, a trailing slash, and filename case. Use for every "is this file already
/// open?" comparison so one file maps to one tab however its path was spelled.
export function samePath(a: string, b: string): boolean {
  return normalizePath(a) === normalizePath(b);
}
