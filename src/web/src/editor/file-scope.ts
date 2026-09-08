import { selectedFileIndex } from "../files/session-files";
import { editorStatus } from "./editor-status-store";
import { isOutsideWorkspace } from "./fs-path";
import { openTabs } from "./session-store";

/**
 * Whether the file the user is currently looking at sits outside the checkout, which is what the editor footer
 * reports. Scratch buffers live outside by design and overlay tabs (web/source/plan) name no workspace path, so
 * neither counts.
 */
export function activeFileOutsideWorkspace(): boolean {
  const path = editorStatus()?.path;
  if (path === undefined) {
    return false;
  }
  const tab = openTabs().find((entry) => entry.path === path);
  if (tab?.scratch === true) {
    return false;
  }
  return isOutsideWorkspace(path, selectedFileIndex().root);
}
