import { defineConfig } from "vite";
import solid from "vite-plugin-solid";

// The built app is served to WKWebView through a custom `app://` scheme handler
// (no network, secure origin). `base: "./"` keeps every asset reference relative
// so it resolves under app://app/. Workers are emitted as classic (iife) scripts,
// which load reliably under a custom scheme.
export default defineConfig({
  plugins: [solid()],
  base: "./",
  worker: {
    format: "iife",
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
