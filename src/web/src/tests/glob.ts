// Translates the test-profile glob subset (**, *, ?, {a,b} alternation, ?(x) optional groups) into an
// anchored RegExp matched against a workspace-relative, forward-slashed path. Mirrors the C# TestRuleMatcher
// across the language boundary — keep the two in sync.

/** Compiles a glob to an anchored RegExp (paths are matched with forward slashes). */
export function globToRegExp(glob: string): RegExp {
  return new RegExp(`^${translate(glob)}$`);
}

/** True if `relativePath` (any separators) matches `glob`. */
export function globMatches(glob: string, relativePath: string): boolean {
  return globToRegExp(glob).test(relativePath.replace(/\\/g, "/"));
}

function translate(glob: string): string {
  let out = "";
  let i = 0;
  while (i < glob.length) {
    const c = glob[i];
    if (c === undefined) {
      break;
    }
    if (c === "*") {
      if (glob[i + 1] === "*") {
        if (glob[i + 2] === "/") {
          out += "(?:.*/)?"; // **/ matches any number of leading directories, including none
          i += 3;
        } else {
          out += ".*"; // ** crosses directory separators
          i += 2;
        }
      } else {
        out += "[^/]*"; // * stays within a path segment
        i += 1;
      }
    } else if (c === "?" && glob[i + 1] === "(") {
      const close = findClose(glob, i + 1);
      if (close >= 0) {
        out += `(?:${translateAlternation(glob.slice(i + 2, close))})?`;
        i = close + 1;
      } else {
        out += "[^/]";
        i += 1;
      }
    } else if (c === "?") {
      out += "[^/]";
      i += 1;
    } else if (c === "{") {
      const close = findClose(glob, i);
      if (close >= 0) {
        out += `(?:${translateAlternation(glob.slice(i + 1, close))})`;
        i = close + 1;
      } else {
        out += escapeRegex("{");
        i += 1;
      }
    } else if (c === "/") {
      out += "/";
      i += 1;
    } else {
      out += escapeRegex(c);
      i += 1;
    }
  }
  return out;
}

function translateAlternation(inner: string): string {
  return splitTopLevel(inner).map(translate).join("|");
}

// Splits on commas not nested inside { } or ( ), so nested alternatives survive.
function splitTopLevel(inner: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let start = 0;
  for (let i = 0; i < inner.length; i++) {
    const c = inner[i];
    if (c === "{" || c === "(") {
      depth++;
    } else if (c === "}" || c === ")") {
      depth--;
    } else if (c === "," && depth === 0) {
      parts.push(inner.slice(start, i));
      start = i + 1;
    }
  }
  parts.push(inner.slice(start));
  return parts;
}

// The index of the bracket closing the one at `open`, honoring nesting, or -1.
function findClose(glob: string, open: number): number {
  const openCh = glob[open];
  const closeCh = openCh === "{" ? "}" : ")";
  let depth = 0;
  for (let i = open; i < glob.length; i++) {
    if (glob[i] === openCh) {
      depth++;
    } else if (glob[i] === closeCh) {
      depth--;
      if (depth === 0) {
        return i;
      }
    }
  }
  return -1;
}

function escapeRegex(c: string): string {
  return c.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
