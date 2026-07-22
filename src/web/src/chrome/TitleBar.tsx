import { Copy, Minus, Square, X } from "lucide-solid";
import { type JSX, Show } from "solid-js";
import type { SymbolActions } from "../symbols/symbol-match";
import { Menu, type MenuAction } from "./Menu";
import { Omnibar } from "./Omnibar";
import { WeavieIcon } from "./WeavieIcon";

type WindowControlAction = "minimize" | "maximize-toggle" | "close";

// The custom Windows title bar, drawn in-web over a frameless host window: logo + menus, omnibar, window
// controls. The background is draggable via CSS `app-region: drag`; interactive regions opt out with
// `no-drag`. Static config (recents, label) comes from `window.__WEAVIE_SHELL__`; live state arrives as props.
export function TitleBar(props: {
  maximized: boolean;
  focused: boolean;
  files: string[];
  filesPending: boolean;
  root: string | null;
  currentFile: string | null;
  onWindowControl: (action: WindowControlAction) => void;
  onMenuAction: (action: MenuAction, path?: string) => void;
  onToggleFiles: () => void;
  onOpenFile: (abs: string, line: number) => void;
  onRequestIndex: () => void;
  symbols: SymbolActions;
}): JSX.Element {
  const shell = window.__WEAVIE_SHELL__;
  const recents = (): string[] => shell?.recents ?? [];
  const label = (): string => shell?.workspaceLabel ?? "weavie";
  const build = (): string => shell?.buildNumber ?? "";

  return (
    <div class="titlebar" classList={{ blurred: !props.focused }}>
      <div class="tb-left">
        <span class="tb-icon" aria-hidden="true">
          <WeavieIcon />
        </span>
        <Menu
          recents={recents()}
          onMenuAction={props.onMenuAction}
          onToggleFiles={props.onToggleFiles}
        />
      </div>

      <div class="tb-center">
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

      <div class="tb-controls">
        <Show when={build()}>
          <span class="tb-build" title={`Weavie ${build()}`}>
            {build()}
          </span>
        </Show>
        <button
          type="button"
          class="tb-ctl"
          aria-label="Minimize"
          onClick={() => props.onWindowControl("minimize")}
        >
          <Minus />
        </button>
        <button
          type="button"
          class="tb-ctl"
          aria-label={props.maximized ? "Restore" : "Maximize"}
          onClick={() => props.onWindowControl("maximize-toggle")}
        >
          <Show when={props.maximized} fallback={<Square />}>
            <Copy />
          </Show>
        </button>
        <button
          type="button"
          class="tb-ctl tb-close"
          aria-label="Close"
          onClick={() => props.onWindowControl("close")}
        >
          <X />
        </button>
      </div>
    </div>
  );
}
