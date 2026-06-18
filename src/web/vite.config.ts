import importMetaUrlPlugin from "@codingame/esbuild-import-meta-url-plugin";
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
// context (both schemes qualify, as does http://localhost — see the dev server below).
export default defineConfig(({ command }) => ({
  plugins: [fixTextmateLazyImport(), solid()],
  // Build: relative base so assets resolve under the custom scheme/virtual-host (no web root).
  // Dev (`serve`): the server hosts from the origin root, where a relative base breaks the HMR
  // client and module URLs — so use an absolute base.
  base: command === "serve" ? "/" : "./",
  // Hot reload: the .NET host (Debug) spawns this dev server itself and points the WebView at it —
  // no second terminal. strictPort so the host can rely on a fixed URL (fail loud if 5173 is taken).
  server: {
    port: 5173,
    strictPort: true,
  },
  // Dev only: monaco-vscode-api's default-extension packages register their assets (theme JSON,
  // the TextMate grammar, onig.wasm) via `new URL('./resource', import.meta.url)`. Vite's *build*
  // rewrites those to emitted assets, but dev pre-bundles the deps with esbuild, which drops the
  // import.meta.url asset targets → 404 (e.g. dark_vs.json) → no theme. This plugin rewrites those
  // URLs during pre-bundling so they resolve. optimizeDeps is dev-only; the build is unaffected.
  optimizeDeps: {
    esbuildOptions: {
      plugins: [importMetaUrlPlugin],
    },
  },
  worker: {
    format: "es",
  },
  build: {
    target: "esnext",
    outDir: "dist",
    emptyOutDir: true,
    assetsInlineLimit: 0,
    chunkSizeWarningLimit: 4096,
    // Multi-page build: emit both the workspace app and the welcome window, sharing common chunks.
    // Paths are relative to the Vite root (src/web), where index.html / welcome.html live.
    rollupOptions: {
      input: {
        main: "index.html",
        welcome: "welcome.html",
      },
    },
    // Even served from disk, the WebView must parse + evaluate every byte of JS before the app runs,
    // and Monaco + the vscode-api layer is multiple megabytes. esbuild minification (near-free at build
    // time) roughly thirds that, cutting startup parse/eval cost — worth it now that launch latency matters.
    minify: "esbuild",
  },
}));
