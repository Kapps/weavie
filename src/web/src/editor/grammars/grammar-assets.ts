// Build-time map of every tm-grammars grammar name -> the URL of its bundled JSON asset. Vite's
// import.meta.glob with `query: "?url"` emits each file as a content-hashed asset and yields its URL; the
// grammar JSON is not bundled into JS but fetched lazily by the TextMate service on first open of a language.
// The glob path must be relative (Vite can't glob a bare package specifier): three levels up to src/web/,
// then into node_modules.

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
