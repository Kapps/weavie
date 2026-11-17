import type { JSX } from "solid-js";
import { onCaptionContextMenu, onCaptionMouseDown } from "../chrome/caption-press";
import { barStyle } from "../chrome/TitleBar";
import { WindowControls } from "../chrome/WindowControls";
import { windowMaximized } from "../chrome/window-state";

// The welcome screen's strip where its window has no OS title bar (Linux): a draggable caption with the
// desktop's window buttons and nothing else.
export function WelcomeBar(props: { chrome: WeavieWindowChrome }): JSX.Element {
  return (
    // biome-ignore lint/a11y/noStaticElementInteractions: window dragging is pointer-only; the OS owns its keyboard path.
    <div
      class="titlebar welcome-bar"
      style={barStyle(props.chrome)}
      data-caption
      onMouseDown={onCaptionMouseDown}
      onContextMenu={onCaptionContextMenu}
    >
      <WindowControls actions={props.chrome.controls.start} maximized={windowMaximized()} />
      <div class="welcome-bar-fill" data-caption />
      <WindowControls actions={props.chrome.controls.end} maximized={windowMaximized()} />
    </div>
  );
}
