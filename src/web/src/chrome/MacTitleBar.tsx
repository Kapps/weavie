import type { JSX } from "solid-js";
import { Omnibar } from "./Omnibar";

// The macOS title-bar strip: the "Go to File" omnibar (center) plus Files/Changes view toggles (left).
// It renders below the native window title bar — macOS provides the traffic-light window controls and the
// system menu bar, so this strip carries only the in-content chrome (the omnibar) that the Windows custom
// title bar draws itself. Gated by the caller on the injected shell config (titleBar === "mac"); unlike the
// Windows bar it has no draggable region (the native title bar owns window dragging) and no window controls.
export function MacTitleBar(props: {
  files: string[];
  root: string | null;
  currentFile: string | null;
  workspaceLabel: string;
  onToggleFiles: () => void;
  onToggleChanges: () => void;
  onOpenFile: (abs: string) => void;
  onRequestIndex: () => void;
}): JSX.Element {
  return (
    <div class="mac-titlebar">
      <div class="mac-tb-left">
        <button type="button" class="mac-tb-btn" onClick={props.onToggleFiles}>
          Files
        </button>
        <button type="button" class="mac-tb-btn" onClick={props.onToggleChanges}>
          Changes
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
