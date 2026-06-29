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

/** The final path segment (file name) of a path, keeping its original casing. */
export function basename(path: string): string {
  const parts = path.split(/[\\/]/).filter((part) => part.length > 0);
  return parts.length > 0 ? (parts[parts.length - 1] as string) : path;
}

/// Path of `path` relative to workspace `root`, keeping the original separators and casing (the prefix is
/// matched case- and separator-insensitively). Returns the file name when they're the same path and the
/// untouched `path` when it lies outside the root — a file with no place under the repo has no repo-relative
/// form, so its own path is the honest answer.
export function repoRelativePath(root: string, path: string): string {
  const normalize = (value: string): string =>
    value.replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
  const normalizedRoot = normalize(root);
  const normalizedPath = normalize(path);
  if (normalizedPath === normalizedRoot) {
    return basename(path);
  }
  if (normalizedPath.startsWith(`${normalizedRoot}/`)) {
    return path.slice(root.replace(/[\\/]+$/, "").length + 1);
  }
  return path;
}
