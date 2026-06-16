import { render } from "solid-js/web";
import App from "./App";
import { log, postToHost } from "./bridge";
import { initEditorServices } from "./editor/vscode-services";
import "./styles.css";

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

// VSCode services (theme/textmate/languages) must initialize before any editor is created, so gate
// the first render on it. On failure, render anyway so the terminal panes still work.
initEditorServices().then(
  () => render(() => <App />, root),
  (err: unknown) => {
    log("error", `editor services init failed: ${String(err)}`);
    render(() => <App />, root);
  },
);
