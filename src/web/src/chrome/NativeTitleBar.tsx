import type { JSX } from "solid-js";
import type { SymbolActions } from "../symbols/symbol-match";
import { Menu } from "./Menu";
import { Omnibar } from "./Omnibar";

// The app bar below a native macOS/Linux window frame. Linux keeps the web command menu; macOS presents it
// in AppKit's system menu bar, so its in-window strip contains only the omnibar.
export function NativeTitleBar(props: {
  showApplicationMenu: boolean;
  files: string[];
  filesPending: boolean;
  root: string | null;
  currentFile: string | null;
  workspaceLabel: string;
  onOpenFile: (abs: string, line: number | undefined) => void;
  onRequestIndex: () => void;
  symbols: SymbolActions;
}): JSX.Element {
  return (
    <div class="native-titlebar">
      <div class="native-tb-left">{props.showApplicationMenu && <Menu />}</div>
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
