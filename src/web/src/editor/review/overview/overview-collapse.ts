import { createSignal } from "solid-js";
import {
  type ClientSession,
  hostConnection,
  LOCAL_BACKEND_ID,
  registerHostFeature,
} from "../../../bridge";
import { currentEditorOptions, onEditorOptionsChanged } from "../../../editor-options";
import type { ReviewOverview } from "../review-store";

// Which overview sections are collapsed. `editor.reviewOverviewCollapse` picks the memory: one app-global set kept
// by the LOCAL host in ~/.weavie/review-overview-state.json, or each session's own, saved with its review.

const [mode, setMode] = createSignal(currentEditorOptions().reviewOverviewCollapse);
onEditorOptionsChanged((options) => setMode(options.reviewOverviewCollapse));

const [global, setGlobal] = createSignal<readonly string[]>([]);

registerHostFeature((connection) => {
  if (!connection.isLocal) return;
  const offHello = connection.onHello((hello) => {
    setGlobal(hello.reviewOverview.collapsed);
  });
  const offState = connection.host
    .feature("reviewOverview")
    .on<{ collapsed: string[] }>("state", (state) => {
      setGlobal(state.collapsed);
    });
  return () => {
    offHello();
    offState();
  };
});

/** Whether `section` is collapsed for the session whose review is `overview`. */
export function sectionCollapsed(overview: ReviewOverview, section: string): boolean {
  return (mode() === "global" ? global() : overview.collapsedSections).includes(section);
}

/** Collapses or expands `section` in whichever memory the setting picks. */
export function setSectionCollapsed(
  session: ClientSession,
  section: string,
  collapsed: boolean,
  setForSession: (session: ClientSession, section: string, collapsed: boolean) => void,
): void {
  if (mode() === "session") {
    setForSession(session, section, collapsed);
    return;
  }
  if (global().includes(section) === collapsed) return;
  setGlobal((ids) => (collapsed ? [...ids, section] : ids.filter((id) => id !== section)));
  hostConnection(LOCAL_BACKEND_ID)
    ?.host.feature("reviewOverview")
    .publish("setCollapsed", { section, collapsed });
}
