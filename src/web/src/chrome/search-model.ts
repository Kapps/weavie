import type { SearchMatch } from "../bridge";

/** How a find-in-files search matches; mirrors the host's GrepOptions (git grep flag mapping). */
export interface SearchOptions {
  caseSensitive: boolean;
  wholeWord: boolean;
  regex: boolean;
  include: string;
  exclude: string;
}

/** Matches grouped by their file path, preserving git grep's order (files in first-seen order, lines within). */
export interface FileGroup {
  path: string;
  matches: SearchMatch[];
}

export function groupByFile(matches: SearchMatch[]): FileGroup[] {
  const groups: FileGroup[] = [];
  const byPath = new Map<string, FileGroup>();
  for (const match of matches) {
    let group = byPath.get(match.path);
    if (group === undefined) {
      group = { path: match.path, matches: [] };
      byPath.set(match.path, group);
      groups.push(group);
    }
    group.matches.push(match);
  }
  return groups;
}

/**
 * Indices into `matches` that are actually rendered (their file group is expanded). Keyboard nav moves over
 * these, so the selection can never wander into a collapsed group and vanish off screen.
 */
export function visibleIndices(matches: SearchMatch[], collapsed: ReadonlySet<string>): number[] {
  const indices: number[] = [];
  matches.forEach((match, i) => {
    if (!collapsed.has(match.path)) {
      indices.push(i);
    }
  });
  return indices;
}

/**
 * The selection after moving `delta` rows over `visible` (clamped at the ends). When `current` itself is
 * hidden by a collapse, lands on the nearest visible row in the move's direction.
 */
export function moveSelection(visible: number[], current: number, delta: number): number {
  const pos = visible.indexOf(current);
  if (pos !== -1) {
    return visible[Math.min(Math.max(pos + delta, 0), visible.length - 1)] ?? current;
  }
  return delta > 0
    ? (visible.find((i) => i > current) ?? visible[visible.length - 1] ?? current)
    : (visible.findLast((i) => i < current) ?? visible[0] ?? current);
}

const wordChar = /\w/;

/**
 * The character indices of `preview` covered by `query` matches, for `<mark>` highlighting (feeds
 * highlightSlice). Literal mode replays git's -F/-i/-w semantics exactly; regex mode approximates its POSIX
 * ERE with a JS RegExp and returns no positions when the pattern doesn't translate — the rows still render,
 * just unhighlighted.
 */
export function matchPositions(
  preview: string,
  query: string,
  options: SearchOptions,
): Set<number> {
  const positions = new Set<number>();
  if (query.length === 0) {
    return positions;
  }
  if (options.regex) {
    let re: RegExp;
    try {
      const source = options.wholeWord ? `\\b(?:${query})\\b` : query;
      re = new RegExp(source, options.caseSensitive ? "g" : "gi");
    } catch {
      return positions;
    }
    for (const m of preview.matchAll(re)) {
      if (m[0].length === 0) {
        break; // a zero-length match would loop forever and highlights nothing anyway
      }
      for (let i = m.index; i < m.index + m[0].length; i++) {
        positions.add(i);
      }
    }
    return positions;
  }

  const haystack = options.caseSensitive ? preview : preview.toLowerCase();
  const needle = options.caseSensitive ? query : query.toLowerCase();
  for (let at = haystack.indexOf(needle); at !== -1; at = haystack.indexOf(needle, at + 1)) {
    const before = preview[at - 1];
    const after = preview[at + needle.length];
    const boundary =
      (before === undefined || !wordChar.test(before)) &&
      (after === undefined || !wordChar.test(after));
    if (!options.wholeWord || boundary) {
      for (let i = at; i < at + needle.length; i++) {
        positions.add(i);
      }
    }
  }
  return positions;
}
