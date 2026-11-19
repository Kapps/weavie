import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import type { FileIndex } from "../files/file-index";
import type { SymbolActions } from "../symbols/symbol-match";
import { onCaptionContextMenu, onCaptionMouseDown } from "./caption-press";
import { Menu } from "./Menu";
import { Omnibar } from "./Omnibar";
import { WeavieIcon } from "./WeavieIcon";
import { WindowControls } from "./WindowControls";

// The window's title bar on every host, drawn from the host's chrome description: menus, omnibar, build
// badge, and whichever window buttons the page draws. The omnibar is centred in the window. Empty bar space is marked `data-caption`; on Windows
// CSS `app-region` makes it the caption, elsewhere a press there goes to the host (caption-press.ts).
export function TitleBar(props: {
  chrome: WeavieWindowChrome;
  maximized: boolean;
  focused: boolean;
  session: ClientSession | null;
  index: FileIndex;
  root: string | null;
  currentFile: string | null;
  onOpenFile: (abs: string, line: number | undefined) => void;
  symbols: (session: ClientSession | null) => SymbolActions;
}): JSX.Element {
  const shell = window.__WEAVIE_SHELL__;
  const label = (): string => shell?.workspaceLabel ?? "weavie";
  const build = (): string => shell?.buildNumber ?? "";

  return (
    // biome-ignore lint/a11y/noStaticElementInteractions: window dragging is pointer-only; the OS owns its keyboard path.
    <div
      class="titlebar"
      classList={{ blurred: !props.focused }}
      style={barStyle(props.chrome)}
      data-caption
      onMouseDown={onCaptionMouseDown}
      onContextMenu={onCaptionContextMenu}
    >
      <div class="tb-left" data-caption>
        <WindowControls actions={props.chrome.controls.start} maximized={props.maximized} />
        <Show when={props.chrome.menus}>
          <span class="tb-icon" aria-hidden="true" data-caption>
            <WeavieIcon />
          </span>
          <Menu />
        </Show>
      </div>

      <div class="tb-center" data-caption>
        <Omnibar
          session={props.session}
          index={props.index}
          root={props.root}
          currentFile={props.currentFile}
          workspaceLabel={label()}
          onOpenFile={props.onOpenFile}
          symbols={props.symbols}
        />
      </div>

      <div class="tb-controls" data-caption>
        <Show when={build()}>
          <span class="tb-build" title={`Weavie ${build()}`} data-caption>
            {build()}
          </span>
        </Show>
        <WindowControls actions={props.chrome.controls.end} maximized={props.maximized} />
      </div>
    </div>
  );
}

/** The bar's measured height and the room its ends leave for buttons the OS draws over it. */
export function barStyle(chrome: WeavieWindowChrome): JSX.CSSProperties {
  return {
    "--titlebar-h": chrome.barHeight === null ? undefined : `${chrome.barHeight}px`,
    "--titlebar-inset-start": `${chrome.inset.start}px`,
    "--titlebar-inset-end": `${chrome.inset.end}px`,
  };
}
