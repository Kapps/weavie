import type { JSX } from "solid-js";
import type { SymbolActions } from "../symbols/symbol-match";
import { Menu } from "./Menu";
import { Omnibar } from "./Omnibar";

// The app bar below a native macOS/Linux window frame. Both use the shared command menu and omnibar while
// the platform owns its window frame; macOS retains only its OS-standard App/Edit/Window system menus.
export function NativeTitleBar(props: {
  files: string[];
  filesPending: boolean;
  root: string | null;
  currentFile: string | null;
  workspaceLabel: string;
  onOpenFile: (abs: string, line: number) => void;
  onRequestIndex: () => void;
  symbols: SymbolActions;
}): JSX.Element {
  return (
    <div class="native-titlebar">
      <div class="native-tb-left">
        <Menu />
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
