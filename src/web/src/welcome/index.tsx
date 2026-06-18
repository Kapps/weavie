import { render } from "solid-js/web";
import { postToHost } from "../bridge";
import { dismissSplash } from "../splash";
import { Welcome } from "./Welcome";
import "./welcome.css";

// Entry for welcome.html — the standalone empty-state window. No bridge listeners, layout, editor, or
// terminals: the host injects the recent workspaces as window.__WEAVIE_WELCOME__ before navigation, and
// the view drives the host back with the existing `menu-action` (open-folder / open-recent) messages.
const root = document.getElementById("root");
if (root === null) {
  throw new Error("missing #root");
}

// Surface uncaught errors to the host log so a headless run can see mount failures.
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
