import { Fzf } from "fzf";

export interface CommandUsage {
  id: string;
  query: string;
  weight: number;
  lastUsed: number;
}

export interface CommandCandidate<T> {
  item: T;
  id: string;
  title: string;
  aliases: readonly string[];
  metadata: string;
}

/** Exact names/aliases, name prefixes, fuzzy names, then metadata. Learning stays inside each tier. */
export function rankCommands<T>(
  candidates: readonly CommandCandidate<T>[],
  query: string,
  usage: readonly CommandUsage[],
) {
  const needle = query.trim().toLowerCase();
  const signals = new Map<string, { overall: number; query: number }>();
  for (const entry of usage) {
    const signal = signals.get(entry.id) ?? { overall: 0, query: 0 };
    if (entry.query === "") signal.overall = entry.weight;
    else if (entry.query === needle) signal.query = entry.weight;
    signals.set(entry.id, signal);
  }
  const searchText = (candidate: CommandCandidate<T>) =>
    `${candidate.title} ${candidate.metadata} ${candidate.aliases.join(" ")}`;
  const texts = [
    ...new Set(
      candidates.flatMap((candidate) => [
        candidate.title,
        ...candidate.aliases,
        searchText(candidate),
      ]),
    ),
  ];
  const matches = new Map(
    needle === "" ? [] : new Fzf(texts).find(needle).map((match) => [match.item, match]),
  );
  const fuzzy = (text: string) => matches.get(text);
  return candidates
    .flatMap((candidate) => {
      const names = [candidate.title, ...candidate.aliases].map((name) => name.toLowerCase());
      const title = needle === "" ? undefined : fuzzy(candidate.title);
      const nameMatches =
        needle === ""
          ? []
          : [candidate.title, ...candidate.aliases]
              .map(fuzzy)
              .filter((match) => match !== undefined);
      const metadata = needle === "" ? undefined : fuzzy(searchText(candidate));
      if (needle !== "" && nameMatches.length === 0 && metadata === undefined) return [];
      const tier =
        needle === "" || names.includes(needle)
          ? 0
          : names.some((name) => name.startsWith(needle))
            ? 1
            : nameMatches.length > 0
              ? 2
              : 3;
      const signal = signals.get(candidate.id) ?? { overall: 0, query: 0 };
      const textScore =
        nameMatches.length === 0
          ? (metadata?.score ?? 0)
          : Math.max(...nameMatches.map((match) => match.score));
      return [
        {
          item: candidate.item,
          positions: title?.positions,
          tier,
          score:
            needle === ""
              ? signal.overall
              : textScore +
                (12 * signal.overall) / (1 + signal.overall) +
                (64 * signal.query) / (1 + signal.query),
          title: candidate.title,
          id: candidate.id,
        },
      ];
    })
    .sort(
      (a, b) =>
        a.tier - b.tier ||
        b.score - a.score ||
        a.title.localeCompare(b.title) ||
        a.id.localeCompare(b.id),
    );
}
