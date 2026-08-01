import { render } from "solid-js/web";
import { log } from "../bridge";
import { dismissSplash } from "../splash";
import { Welcome } from "./Welcome";
import "../fonts.css";
import "./welcome.css";

// Entry for welcome.html — the standalone empty-state window. The host injects recents as
// window.__WEAVIE_WELCOME__ before navigation; the view drives back through the host window feature.
const root = document.getElementById("root");
if (root === null) {
  throw new Error("missing #root");
}

// Forward uncaught errors to the host log — an embedded WebView has no easy devtools, so this is the only
// place a mount failure becomes visible.
window.addEventListener("error", (e) => {
  log("error", `window.error: ${e.message} @ ${e.filename}:${e.lineno}:${e.colno}`);
});

render(() => <Welcome />, root);
dismissSplash();
