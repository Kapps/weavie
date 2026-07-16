import { render } from "solid-js/web";
import App from "./App";
import { currentEditorBinding, pageId, postToHost, releaseEditorBinding } from "./bridge";
import { mark } from "./startup-timing";
import "./fonts.css";
// Chrome stylesheets, co-located with the components they style. Order is the cascade: base first, then
// per-feature; confirm-dialog.css must precede new-session-prompt.css (the prompt scopes overrides onto it).
import "./styles.css";
import "./layout/layout.css";
import "./agent/agent.css";
import "./chrome/session-rail.css";
import "./chrome/context-menu.css";
import "./editor/editor.css";
import "./terminal/terminal.css";
import "./editor/diff.css";
import "./editor/comment-prose.css";
import "./editor/preview/preview.css";
import "./editor/preview/preview-highlight.css";
import "./editor/preview/embed-zoom.css";
import "./editor/preview/embed-lightbox.css";
import "./files/files.css";
import "./chrome/search-panel.css";
import "./chrome/titlebar.css";
import "./chrome/omnibar.css";
import "./chrome/resize-frame.css";
import "./notify/notify.css";
import "./notify/suggestions.css";
import "./editor/confirm-dialog.css";
import "./chrome/new-session-prompt.css";

mark("module-eval");

const root = document.getElementById("root");
if (root === null) {
  throw new Error("missing #root");
}

// Forward uncaught errors + promise rejections to the host log — an embedded WebView has no easy devtools,
// so this is the only place a mount failure or stray rejection becomes visible.
window.addEventListener("error", (e) => {
  postToHost({
    type: "log",
    level: "error",
    message: `window.error: ${e.message} @ ${e.filename}:${e.lineno}:${e.colno}`,
  });
});
window.addEventListener("unhandledrejection", (e) => {
  const r = e.reason;
  const message = r instanceof Error ? (r.stack ?? r.message) : String(r);
  postToHost({ type: "log", level: "error", message: `unhandledrejection: ${message}` });
});
window.addEventListener("pagehide", () => {
  const binding = currentEditorBinding();
  if (binding !== null) {
    releaseEditorBinding(binding);
  }
});

// Render the shell immediately. Monaco + its VSCode service layer load as a separate chunk from inside App,
// so first paint doesn't wait on the multi-megabyte editor code. The splash stays up until App dismisses it
// once the editor is ready.
render(() => <App />, root);

// `ready` asks the host for an immediate state replay, so every App listener must exist before it is sent.
postToHost({ type: "ready", pageId });
// Editor ownership is explicit: background backend `ready` handshakes may refresh ambient state but can never
// mute or steal the page's mounted editor projection.
postToHost({ type: "acquire-editor", pageId });
