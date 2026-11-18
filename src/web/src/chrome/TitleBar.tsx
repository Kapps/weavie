import { createSignal, type JSX, onCleanup, Show } from "solid-js";
import type { SymbolActions } from "../symbols/symbol-match";
import { Menu } from "./Menu";
import { Omnibar } from "./Omnibar";
import { WeavieIcon } from "./WeavieIcon";
import { WindowControls } from "./WindowControls";

// The window's title bar on every host, drawn from its chrome description; empty space is `data-caption`
// (caption-press.ts), and the omnibar is placed from the sides' measured widths (titlebar.css).
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
  let left: HTMLElement | undefined;
  let right: HTMLElement | undefined;
  const [sides, setSides] = createSignal({ left: 0, right: 0 });
  // The grid places the omnibar from each side's content width (titlebar.css).
  const observer = new ResizeObserver(() =>
    setSides({ left: left?.offsetWidth ?? 0, right: right?.offsetWidth ?? 0 }),
  );
  onCleanup(() => observer.disconnect());

  return (
    <div
      class="titlebar"
      classList={{ blurred: !props.focused, windowed: props.chrome.caption !== "none" }}
      style={{
        ...barStyle(props.chrome),
        "--titlebar-left": `${sides().left}px`,
        "--titlebar-right": `${sides().right}px`,
      }}
      data-caption
    >
      <div
        class="tb-left"
        data-caption
        ref={(el) => {
          left = el;
          observer.observe(el);
        }}
      >
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

      <div
        class="tb-controls"
        data-caption
        ref={(el) => {
          right = el;
          observer.observe(el);
        }}
      >
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
