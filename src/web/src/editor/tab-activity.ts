import { createMemo } from "solid-js";
import { selectedSession } from "../bridge";
import { activeTabFor } from "./session-store";
import type { TabOwner } from "./tab-owner";

/** Selection is reactive; lifetime checks remain immediate during synchronous feature teardown. */
export function createTabActivity(tab: TabOwner): () => boolean {
  const selected = createMemo(
    () => selectedSession() === tab.session && activeTabFor(tab.session) === tab,
  );
  return () => !tab.signal.aborted && !tab.session.signal.aborted && selected();
}
