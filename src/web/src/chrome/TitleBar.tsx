import { createSignal, type JSX, onCleanup, Show } from "solid-js";
import type { SymbolActions } from "../symbols/symbol-match";
import { Menu } from "./Menu";
import { Omnibar } from "./Omnibar";
import { WeavieIcon } from "./WeavieIcon";
import { WindowControls } from "./WindowControls";

// The window's title bar on every host, drawn from its chrome description; empty space is `data-caption`
// (caption-press.ts), and the omnibar stays centred in the window (barStyle).
export function TitleBar(props: {
  chrome: WeavieWindowChrome;
  maximized: boolean;
  focused: boolean;
  files: string[];
  filesPending: boolean;
  root: string | null;
  currentFile: string | null;
  onOpenFile: (abs: string, line: number | undefined) => void;
  onRequestIndex: () => void;
  symbols: () => SymbolActions;
}): JSX.Element {
  const shell = window.__WEAVIE_SHELL__;
  const label = (): string => shell?.workspaceLabel ?? "weavie";
  const build = (): string => shell?.buildNumber ?? "";
  const [side, setSide] = createSignal(0);
  const sides: HTMLElement[] = [];
  // Both side columns take the wider side's width, so the omnibar narrows before it leaves the centre.
  const observer = new ResizeObserver(() =>
    setSide(Math.max(...sides.map((el) => el.offsetWidth))),
  );
  const measure = (el: HTMLElement): void => {
    sides.push(el);
    observer.observe(el);
  };
  onCleanup(() => observer.disconnect());

  return (
    <div
      class="titlebar"
      classList={{ blurred: !props.focused, windowed: props.chrome.caption !== "none" }}
      style={{ ...barStyle(props.chrome), "--titlebar-side": `${side()}px` }}
      data-caption
    >
      <div class="tb-left" data-caption ref={measure}>
        <WindowControls actions={props.chrome.controls.start} maximized={props.maximized} />
        <Show when={props.chrome.menus}>
          <span class="tb-icon" aria-hidden="true" data-caption>
            <WeavieIcon size={18} />
          </span>
          <Menu />
        </Show>
      </div>

      <div class="tb-center" data-caption>
        <Omnibar
          files={props.files}
          filesPending={props.filesPending}
          root={props.root}
          currentFile={props.currentFile}
          workspaceLabel={label()}
          onOpenFile={props.onOpenFile}
          onRequestIndex={props.onRequestIndex}
          symbols={props.symbols}
        />
      </div>

      <div class="tb-controls" data-caption ref={measure}>
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
