// Lowercase the drive letter before building a `file://` URI. The file:// scheme matches case-sensitively and
// VSCode's convention is a lowercase drive, so without this a `C:`-spelled change event would never match an
// open `c:`-spelled model and the working copy would never reload.
export function canonicalFsPath(path: string): string {
  return path.replace(/^([A-Za-z]):/, (_match, drive: string) => `${drive.toLowerCase()}:`);
}

// A looser identity key for "same file?" comparison only — never to open, read, display, or persist. Folds
// the WSL `/mnt/<drive>/` mount onto `<drive>:`, unifies separators, drops a trailing slash, and lowercases.
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

/// True when both strings name the same on-disk file, ignoring drive-letter case, separators, the WSL
/// `/mnt/<drive>/` prefix, a trailing slash, and filename case. Use for every "already open?" tab comparison.
export function samePath(a: string, b: string): boolean {
  return normalizePath(a) === normalizePath(b);
}
