// The shared, minimal language-configuration for broad-highlighted languages. tm-grammars ships grammars
// only (no language-configuration), so without this the tail languages would lose bracket matching /
// auto-close. We provide one generic config (brackets + auto-close + surrounding pairs) reused by every
// broad language — `'` is deliberately not auto-closed (it breaks Rust lifetimes / OCaml / Lisp), and no
// `comments` are declared (comment syntax varies too much to guess; the curated packs keep correct
// comment-toggle). The JSON is emitted as a bundled asset and fetched on demand, like the grammars.

/** The manifest-relative virtual path each broad extension references for its language configuration. */
export const GENERIC_CONFIGURATION_PATH = "./language-configuration.json";

/** Bundled asset URL of the shared generic language-configuration JSON. */
export const genericConfigurationUrl = new URL(
  "./generic-language-configuration.json",
  import.meta.url,
).toString();
