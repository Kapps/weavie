import { type Plugin, defineConfig } from "vite";
import solid from "vite-plugin-solid";

// monaco-vscode-api lazy-loads vscode-textmate's incremental-tokenization helpers (applyStateStackDiff,
// diffStateStacksRefEq, INITIAL) via `import('…/_virtual/main').then(n => n.main)`. Vite's build-time
// optimization of the `import(literal).then(m => m.prop)` shape flattens the dynamic import to the raw
// vscode-textmate CJS chunk — bypassing the `_virtual/main` wrapper that actually exposes those names —
// so they come back undefined and background tokenization throws (breaking syntax highlighting on edit).
// The fix: rewrite the fragile dynamic import to a plain static namespace import (which the bundler
// resolves correctly), preserving the `.main` indirection the library relies on.
function fixTextmateLazyImport(): Plugin {
  const ns = "__weavieTextmateMainNs";
  const staticImport = `import * as ${ns} from "@codingame/monaco-vscode-api/_virtual/main";\n`;
  const dynamicImport =
    /import\(['"]@codingame\/monaco-vscode-api\/_virtual\/main['"]\)\.then\(function \(n\) \{ return n\.main; \}\)/g;
  const replacement = `Promise.resolve(${ns}.main ?? ${ns})`;
  return {
    name: "weavie-fix-textmate-lazy-import",
    enforce: "pre",
    transform(code, id) {
      if (!id.includes("monaco-vscode-textmate-service-override") || !dynamicImport.test(code)) {
        return null;
      }
      return { code: staticImport + code.replace(dynamicImport, replacement), map: null };
    },
  };
}

// The built app is served to the WebView through a custom scheme handler (macOS `app://`, Windows
// `https://weavie.app`) — no network, secure origin. `base: "./"` keeps every asset reference relative
// so it resolves under the scheme. Workers are ES modules: the monaco-vscode-api textmate worker is a
// code-splitting build, which Rollup cannot emit as iife/umd. Module workers are fine under a secure
// context (both schemes qualify).
export default defineConfig({
  plugins: [fixTextmateLazyImport(), solid()],
  base: "./",
  worker: {
    format: "es",
  },
  build: {
    target: "esnext",
    outDir: "dist",
    emptyOutDir: true,
    assetsInlineLimit: 0,
    chunkSizeWarningLimit: 4096,
    // Local prototype served from disk — minification only adds build time.
    minify: false,
  },
});
