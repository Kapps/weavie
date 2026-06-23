import { render } from "solid-js/web";
import App from "./App";
import { postToHost } from "./bridge";
import { mark } from "./startup-timing";
import "./fonts.css";
// Chrome stylesheets, split out of the former monolithic styles.css and co-located with the
// components they style. Import order preserves the cascade: base first, then per-feature sheets.
// confirm-dialog.css must precede new-session-prompt.css (the prompt scopes overrides onto it).
import "./styles.css";
import "./layout/layout.css";
import "./chrome/session-rail.css";
import "./chrome/context-menu.css";
import "./editor/editor.css";
import "./terminal/terminal.css";
import "./editor/diff.css";
import "./editor/comment-prose.css";
import "./editor/preview/preview.css";
import "./files/files.css";
import "./chrome/titlebar.css";
import "./chrome/omnibar.css";
import "./chrome/resize-frame.css";
import "./notify/notify.css";
import "./editor/confirm-dialog.css";
import "./chrome/new-session-prompt.css";

mark("module-eval");

const root = document.getElementById("root");
if (root === null) {
  throw new Error("missing #root");
}

// Forward uncaught errors + promise rejections to the host log — an embedded WebView has no easy
// devtools, so this is the only place a mount failure or stray rejection becomes visible.
window.addEventListener("error", (e) => {
  postToHost({
    type: "log",
    level: "error",
    message: `window.error: ${e.message} @ ${e.filename}:${e.lineno}:${e.colno}`,
  });
});
window.addEventListener("unhandledrejection", (e) => {
  postToHost({ type: "log", level: "error", message: `unhandledrejection: ${String(e.reason)}` });
});

postToHost({ type: "ready" });

// Render the shell immediately. The Monaco editor and its VSCode service layer load as a separate chunk
// from inside App (see editor-host), so first paint doesn't wait on the multi-megabyte editor code to
// download and initialize. The splash stays up (see splash.ts) until App dismisses it once the editor is
// ready, giving the user a single dark → app reveal.
render(() => <App />, root);
