import type { JSX } from "solid-js";
import { WindowControls } from "../chrome/WindowControls";
import { hostWindowFocused, windowMaximized } from "../chrome/window-state";

// The welcome screen's strip where its window has no OS title bar (Linux): a caption with the desktop's buttons.
export function WelcomeBar(props: { chrome: WeavieWindowChrome }): JSX.Element {
  return (
    <div
      class="titlebar welcome-bar"
      classList={{ blurred: !hostWindowFocused() }}
      style={{
        "--titlebar-h": props.chrome.barHeight === null ? undefined : `${props.chrome.barHeight}px`,
      }}
      data-caption
    >
      <WindowControls actions={props.chrome.controls.start} maximized={windowMaximized()} />
      <div class="welcome-bar-fill" data-caption />
      <WindowControls actions={props.chrome.controls.end} maximized={windowMaximized()} />
    </div>
  );
}
