import type { ClientSession } from "../bridge";
import { fileIndexFor } from "../files/session-file-index";
import { isOutsideWorkspace } from "./fs-path";
import { activePathFor, openTabsFor } from "./session-store";
import { isFileTab } from "./tab-entry";

/**
 * Whether `session`'s active file sits outside the checkout, which is what the editor footer
 * reports. Scratch buffers live outside by design and overlay tabs (web/source/plan) name no workspace path, so
 * neither counts.
 */
export function activeFileOutsideWorkspace(session: ClientSession | null): boolean {
  const path = activePathFor(session);
  if (path === null) {
    return false;
  }
  const tab = openTabsFor(session).find((entry) => entry.path === path);
  if (tab === undefined || tab.scratch === true || !isFileTab(tab)) {
    return false;
  }
  return isOutsideWorkspace(path, fileIndexFor(session).root);
}
