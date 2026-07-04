import importMetaUrlPlugin from "@codingame/esbuild-import-meta-url-plugin";
import { defineConfig, type Plugin } from "vite";
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
// `https://weavie.dev`) — no network, secure origin. `base: "./"` keeps every asset reference relative
// so it resolves under the scheme. Workers are ES modules: the monaco-vscode-api textmate worker is a
// code-splitting build, which Rollup cannot emit as iife/umd. Module workers are fine under a secure
// context (both schemes qualify, as does http://localhost — see the dev server below).
export default defineConfig(({ command }) => ({
  plugins: [fixTextmateLazyImport(), solid()],
  // Build: relative base so assets resolve under the custom scheme/virtual-host (no web root).
  // Dev (`serve`): the server hosts from the origin root, where a relative base breaks the HMR
  // client and module URLs — so use an absolute base.
  base: command === "serve" ? "/" : "./",
  // Hot reload: the .NET host (Debug) spawns this dev server itself and points its WebView at it — no second
  // terminal. The host assigns a *per-instance* free port via `--port N --strictPort`, so multiple worktrees /
  // Debug instances each get their own isolated server instead of colliding on (and silently reusing) one fixed
  // 5173. No `port` is pinned here; strictPort keeps a bare `vite` run failing loud rather than wandering ports.
  server: {
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
      output: {
        // Split the ~7 MB Monaco + vscode-api layer into its own chunk. Nothing on the first-paint path
        // imports it — the editor is brought up lazily (a deferred dynamic import in editor-controller) —
        // but editor-controller (eager) dynamically imports THREE consumers of it (editor-host, inline-diff,
        // comment-prose), so Rollup's default splitting hoists the shared Monaco into their common ancestor:
        // the eager entry chunk. That forces the WebView to parse all 7 MB before the shell can render.
        // Pinning it to a named chunk keeps it out of the entry, so first paint parses only the shell and
        // Monaco loads with the deferred editor bring-up. xterm stays eager (the terminals paint on boot).
        manualChunks(id) {
          if (
            /[\\/]node_modules[\\/](monaco-editor|monaco-languageclient|@codingame[\\/]monaco-vscode)/.test(
              id,
            )
          ) {
            return "monaco";
          }
          return undefined;
        },
      },
    },
    // Even served from disk, the WebView must parse + evaluate every byte of JS before the app runs,
    // and Monaco + the vscode-api layer is multiple megabytes. esbuild minification (near-free at build
    // time) roughly thirds that, cutting startup parse/eval cost — worth it now that launch latency matters.
    minify: "esbuild",
  },
}));
