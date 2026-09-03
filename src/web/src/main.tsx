import { render } from "solid-js/web";
import App from "./App";
import { hostConnection, isBrowserHostedShell, LOCAL_BACKEND_ID, log } from "./bridge";
import { mark } from "./startup-timing";
import "./fonts.css";
// Chrome stylesheets, co-located with the components they style. Order is the cascade: base first, then
// per-feature.
import "./styles.css";
import "./layout/layout.css";
import "./agent/agent.css";
import "./chrome/session-rail.css";
import "./chrome/context-menu.css";
import "./chrome/tabs.css";
import "./editor/editor.css";
import "./terminal/terminal.css";
import "./editor/diff.css";
import "./editor/review/unified-review.css";
import "./editor/comment-prose.css";
import "./editor/revise.css";
import "./editor/git-blame.css";
import "./editor/preview/preview.css";
import "./editor/preview/preview-highlight.css";
import "./editor/preview/embed-zoom.css";
import "./editor/preview/embed-lightbox.css";
import "./files/files.css";
import "./chrome/search-panel.css";
import "./chrome/titlebar.css";
import "./chrome/omnibar.css";
import "./chrome/resize-frame.css";
import "./chrome/middle-click-autoscroll.css";
import "./notify/notify.css";
import "./notify/suggestions.css";
import "./editor/confirm-dialog.css";
import "./chrome/session-prompt.css";
import "./mobile/mobile.css";
import "./mobile/session-inbox.css";

mark("module-eval");

const root = document.getElementById("root");
if (root === null) {
  throw new Error("missing #root");
}

// Forward uncaught errors + promise rejections to the host log — an embedded WebView has no easy devtools,
// so this is the only place a mount failure or stray rejection becomes visible.
window.addEventListener("error", (e) => {
  log("error", `window.error: ${e.message} @ ${e.filename}:${e.lineno}:${e.colno}`);
});
window.addEventListener("unhandledrejection", (e) => {
  const r = e.reason;
  const message = r instanceof Error ? (r.stack ?? r.message) : String(r);
  log("error", `unhandledrejection: ${message}`);
});
window.__WEAVIE_CLEAR_BOOT_ERROR_CAPTURE__?.();
// Render the shell immediately. Monaco + its VSCode service layer load as a separate chunk from inside App,
// so first paint doesn't wait on the multi-megabyte editor code. The splash stays up until App dismisses it
// once the editor is ready.
render(() => <App />, root);

const localConnection = hostConnection(LOCAL_BACKEND_ID);
if (localConnection !== undefined && !isBrowserHostedShell()) {
  void localConnection.connect().catch((error: unknown) => localConnection.reportError(error));
}
