// Shared minimal language-configuration (brackets + auto-close + surrounding pairs) for broad-highlighted
// languages. `'` is not auto-closed (breaks Rust lifetimes / OCaml / Lisp) and no `comments` are declared
// (varies too much to guess). Emitted as a bundled asset.

/** The manifest-relative virtual path each broad extension references for its language configuration. */
export const GENERIC_CONFIGURATION_PATH = "./language-configuration.json";

/** Bundled asset URL of the shared generic language-configuration JSON. */
export const genericConfigurationUrl = new URL(
  "./generic-language-configuration.json",
  import.meta.url,
).toString();
