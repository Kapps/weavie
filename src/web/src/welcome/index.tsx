import { Show } from "solid-js";
import { render } from "solid-js/web";
import { log } from "../bridge";
import "../fonts";
import { windowChrome } from "../chrome/window-state";
import { dismissSplash } from "../splash";
import { applyChromeTheme } from "../theme/controller";
import { ThemePicker } from "../theme/ThemePicker";
import { Welcome } from "./Welcome";
import { WelcomeBar } from "./WelcomeBar";
import "../fonts.css";
import "../styles.css";
import "../primitives.css";
import "../chrome/session-prompt.css";
import "../chrome/titlebar.css";
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

applyChromeTheme();
render(
  () => (
    <>
      <div class="welcome-window">
        <Show when={windowChrome()?.caption === "press" && windowChrome()}>
          {(chrome) => <WelcomeBar chrome={chrome()} />}
        </Show>
        <Welcome />
      </div>
      <ThemePicker />
    </>
  ),
  root,
);
dismissSplash();
