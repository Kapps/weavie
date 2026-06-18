// Build-time map of every tm-grammars grammar name -> the URL of its bundled JSON asset.
//
// Vite's import.meta.glob with `query: "?url"` emits each matched file as a content-hashed asset in the
// build (and serves it in dev) and gives back its URL string — the same "ship a runtime-fetched asset"
// mechanism the @codingame default-extension packages use via `new URL(..., import.meta.url)`. The
// grammar JSON is NOT bundled into JS; the TextMate service fetches each one lazily, from the local app
// bundle, the first time a file of that language is opened (no network).
//
// The glob path is RELATIVE (Vite can't glob a bare package specifier) and depends on this file's
// location: from src/editor/grammars/ it is three levels up to src/web/, then into the flat node_modules.

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
