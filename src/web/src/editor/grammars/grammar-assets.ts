// Build-time map of every tm-grammars grammar name -> its bundled JSON asset URL (via `?url`, so the grammar
// is fetched lazily by the TextMate service rather than bundled into JS). The glob path must be relative —
// Vite can't glob a bare package specifier.

const modules = import.meta.glob("../../../node_modules/tm-grammars/grammars/*.json", {
  query: "?url",
  import: "default",
  eager: true,
}) as Record<string, string>;

/** Grammar name (the tm-grammars file basename, e.g. "rust") -> bundled asset URL of its `.tmLanguage` JSON. */
export const grammarUrlByName: Record<string, string> = {};
for (const [path, url] of Object.entries(modules)) {
  const name = path.slice(path.lastIndexOf("/") + 1, -".json".length);
  grammarUrlByName[name] = url;
}
