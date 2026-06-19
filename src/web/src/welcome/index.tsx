import { render } from "solid-js/web";
import { postToHost } from "../bridge";
import { dismissSplash } from "../splash";
import { Welcome } from "./Welcome";
import "../fonts.css";
import "./welcome.css";

// Entry for welcome.html — the standalone empty-state window. No bridge listeners, layout, editor, or
// terminals: the host injects the recent workspaces as window.__WEAVIE_WELCOME__ before navigation, and
// the view drives the host back with the existing `menu-action` (open-folder / open-recent) messages.
const root = document.getElementById("root");
if (root === null) {
  throw new Error("missing #root");
}

// Forward uncaught errors to the host log — an embedded WebView has no easy devtools, so this is the
// only place a mount failure becomes visible.
window.addEventListener("error", (e) => {
  postToHost({
    type: "log",
    level: "error",
    message: `window.error: ${e.message} @ ${e.filename}:${e.lineno}:${e.colno}`,
  });
});

postToHost({ type: "ready" });
render(() => <Welcome />, root);
dismissSplash();
