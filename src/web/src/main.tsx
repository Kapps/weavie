import { render } from "solid-js/web";
import App from "./App";
import { postToHost } from "./bridge";
import { mark } from "./startup-timing";
import "./fonts.css";
import "./styles.css";

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
