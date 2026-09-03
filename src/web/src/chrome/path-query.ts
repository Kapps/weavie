// Open-by-path: recognizing a filesystem path typed into the omnibar and splitting it into the directory to
// list and the partial leaf to filter by. Pure — the mode predicate and the row source both read from here, so
// "is this path mode?" and "what does it resolve to?" can never disagree.

// Anything the user could mean as a path rather than a fuzzy query: POSIX-absolute, home-relative, a Windows
// drive, a UNC share, or an explicitly-relative "./" / "../". A bare "src/foo" stays a fuzzy query.
const PATH_SHAPE = /^(\/|~(\/|\\|$)|[A-Za-z]:[\\/]|\\\\|\.\.?(\/|\\))/;

/** The separator `path` is spelled with: backslash only for a Windows path that uses no forward slash. */
export function separatorFor(path: string): string {
  return path.includes("\\") && !path.includes("/") ? "\\" : "/";
}

/**
 * The open-by-path seed for `root`: the directory itself, so the first keystroke appends rather than
 * replacing. Empty when the root is unknown, which reads as an ordinary empty query.
 */
export function pathSeed(root: string): string {
  return root === "" ? "" : root + separatorFor(root);
}

/** Whether `query` is shaped like a filesystem path, and so selects the omnibar's path mode. */
export function looksLikePath(query: string): boolean {
  return PATH_SHAPE.test(query.trimStart());
}

/** A parsed path query: the full path it names, the directory to list, and the partial leaf to filter by. */
export interface ParsedPath {
  absolute: string;
  dir: string;
  leaf: string;
}

/**
 * Resolves `query` against the host's `root` and `home`, returning the absolute path it names plus the
 * directory/leaf split that drives completion. A trailing separator means the leaf is empty — the user is asking
 * for that directory's whole contents. Returns null when the query isn't path-shaped, or names `~` before the
 * host has told us where home is — resolving it against the worktree would name a directory nobody meant.
 */
export function parsePathQuery(
  query: string,
  context: { root: string; home: string | null },
): ParsedPath | null {
  const raw = query.trimStart();
  if (!looksLikePath(raw)) {
    return null;
  }

  const sep = separatorFor(context.root);
  const expanded = expandHome(raw, context.home);
  if (expanded === null) {
    return null;
  }
  const absolute = isRooted(expanded) ? expanded : join(context.root, expanded, sep);
  const cut = Math.max(absolute.lastIndexOf("/"), absolute.lastIndexOf("\\"));
  return cut < 0
    ? { absolute, dir: context.root, leaf: absolute }
    : {
        absolute,
        dir: asDirectory(absolute.slice(0, cut), separatorFor(absolute)),
        leaf: absolute.slice(cut + 1),
      };
}

// A prefix that names a filesystem root needs its separator back to be a directory: "" is the POSIX root and
// "C:" is a Windows drive, which without the separator means "the current directory on drive C" instead.
function asDirectory(prefix: string, sep: string): string {
  return prefix === "" || /^[A-Za-z]:$/.test(prefix) ? prefix + sep : prefix;
}

function expandHome(path: string, home: string | null): string | null {
  if (!(path === "~" || path.startsWith("~/") || path.startsWith("~\\"))) {
    return path;
  }
  return home === null ? null : home + path.slice(1);
}

function isRooted(path: string): boolean {
  return /^(\/|[A-Za-z]:[\\/]|\\\\)/.test(path);
}

// Resolves "./" and "../" segments against the base, so "../sibling" from a worktree names the sibling directory.
function join(base: string, relative: string, sep: string): string {
  const segments = base.split(/[\\/]/);
  for (const segment of relative.split(/[\\/]/)) {
    if (segment === "..") {
      segments.pop();
    } else if (segment !== "." && segment !== "") {
      segments.push(segment);
    }
  }
  // A trailing separator in the input is meaningful (list this directory), so preserve it. Popping past the
  // filesystem root leaves a bare prefix, which asDirectory turns back into the root itself.
  const trailing = /[\\/]$/.test(relative) ? sep : "";
  return asDirectory(segments.join(sep), sep) + trailing;
}
