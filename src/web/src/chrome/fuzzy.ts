// Tiny fuzzy matcher for the omnibar — no dependency. Walks the query as a subsequence of the candidate:
// every query char must appear, in order. Contiguous runs, word/segment boundaries, and matches inside the
// file name (after the last slash) score higher, so "tb.tsx" ranks TitleBar.tsx above a deep path that
// merely contains those letters. Returns null when the query isn't a subsequence at all.

export interface FuzzyMatch {
  /** Higher is a better match. */
  score: number;
  /** Indices in the candidate that matched, for optional highlighting. */
  positions: number[];
}

export function fuzzyMatch(query: string, candidate: string): FuzzyMatch | null {
  if (query.length === 0) {
    return { score: 0, positions: [] };
  }

  const q = query.toLowerCase();
  const c = candidate.toLowerCase();
  // Where the file name starts (after the last separator) — matches here are worth more.
  const nameStart = Math.max(c.lastIndexOf("/"), c.lastIndexOf("\\")) + 1;

  let qi = 0;
  let score = 0;
  let prev = -2;
  const positions: number[] = [];
  for (let ci = 0; ci < c.length && qi < q.length; ci++) {
    if (c[ci] !== q[qi]) {
      continue;
    }
    let s = 1;
    if (ci === prev + 1) {
      s += 4; // contiguous with the previous match
    }
    const before = ci > 0 ? c[ci - 1] : "/";
    if (before === "/" || before === "\\" || before === "." || before === "-" || before === "_") {
      s += 3; // start of a path segment or word
    }
    if (ci >= nameStart) {
      s += 2; // inside the file name
    }
    score += s;
    positions.push(ci);
    prev = ci;
    qi++;
  }

  if (qi < q.length) {
    return null; // not every query char was consumed
  }

  // Gently prefer shorter candidates (denser match) as a tie-breaker.
  return { score: score - c.length * 0.01, positions };
}
