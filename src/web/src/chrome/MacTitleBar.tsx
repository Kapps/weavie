import type { JSX } from "solid-js";
import { Omnibar } from "./Omnibar";

// The macOS title-bar strip: omnibar (center) plus Files toggle (left). Renders below the native title bar,
// so it carries no draggable region or window controls. Gated by the caller on titleBar === "mac".
export function MacTitleBar(props: {
  files: string[];
  root: string | null;
  currentFile: string | null;
  workspaceLabel: string;
  onToggleFiles: () => void;
  onOpenFile: (abs: string) => void;
  onRequestIndex: () => void;
}): JSX.Element {
  return (
    <div class="mac-titlebar">
      <div class="mac-tb-left">
        <button type="button" class="mac-tb-btn" onClick={props.onToggleFiles}>
          Files
        </button>
      </div>
      <div class="tb-center">
        <Omnibar
          files={props.files}
          root={props.root}
          currentFile={props.currentFile}
          workspaceLabel={props.workspaceLabel}
          onOpenFile={props.onOpenFile}
          onRequestIndex={props.onRequestIndex}
        />
      </div>
      <div class="mac-tb-right" />
    </div>
  );
}
