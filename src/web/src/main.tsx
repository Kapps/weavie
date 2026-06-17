import { render } from "solid-js/web";
import App from "./App";
import { postToHost } from "./bridge";
import { mark } from "./startup-timing";
import "./styles.css";

mark("module-eval");

const root = document.getElementById("root");
if (root === null) {
  throw new Error("missing #root");
}

// TEMP diagnostic: surface uncaught errors to the host log so a headless run can see mount failures.
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

// Render the shell immediately. The Monaco editor and its VSCode service layer load as a separate
// chunk from inside App (see editor-host), so first paint no longer waits on the multi-megabyte editor
// code to download, parse, and initialize. The splash stays up (see splash.ts) and App fades it out
// once the editor is ready, so the user gets a single dark → app reveal rather than a placeholder relay.
render(() => <App />, root);
