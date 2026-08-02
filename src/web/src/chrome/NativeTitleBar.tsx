import type { JSX } from "solid-js";
import { LOCAL_BACKEND_ID } from "../bridge";
import { keyLabelInCatalog } from "../commands/key-hint";
import { runCommandFromCatalogWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { SymbolActions } from "../symbols/symbol-match";
import { Menu } from "./Menu";
import { Omnibar } from "./Omnibar";

// The app bar below a native macOS/Linux window frame. macOS owns its system menu and gets a Files button;
// Linux gets the web File/View menus. Both share the omnibar and omit web window controls.
export function NativeTitleBar(props: {
  platform: "mac" | "linux";
  files: string[];
  filesPending: boolean;
  root: string | null;
  currentFile: string | null;
  workspaceLabel: string;
  recents: string[];
  onOpenFile: (abs: string, line: number) => void;
  onRequestIndex: () => void;
  symbols: SymbolActions;
}): JSX.Element {
  const filesKeys = (): string => keyLabelInCatalog(LOCAL_BACKEND_ID, CommandIds.toggleFileBrowser);
  const toggleFiles = (): void => {
    void runCommandFromCatalogWithFeedback(LOCAL_BACKEND_ID, CommandIds.toggleFileBrowser);
  };

  return (
    <div class="native-titlebar">
      <div class="native-tb-left">
        {props.platform === "linux" ? (
          <Menu recents={props.recents} />
        ) : (
          <button
            type="button"
            class="native-tb-btn"
            title={`Files${filesKeys().length > 0 ? ` (${filesKeys()})` : ""}`}
            onClick={toggleFiles}
          >
            Files
          </button>
        )}
      </div>
      <div class="tb-center">
        <Omnibar
          files={props.files}
          filesPending={props.filesPending}
          root={props.root}
          currentFile={props.currentFile}
          workspaceLabel={props.workspaceLabel}
          onOpenFile={props.onOpenFile}
          onRequestIndex={props.onRequestIndex}
          symbols={props.symbols}
        />
      </div>
      <div class="native-tb-right" />
    </div>
  );
}
