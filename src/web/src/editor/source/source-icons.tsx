import { GraduationCap, Logs } from "lucide-solid";
import type { JSX } from "solid-js";
import type { ClientSession } from "../../messaging/host-connection";
import { NotionIcon } from "./NotionIcon";
import { sourceDoc } from "./source-store";

// Tab icons keyed by the source id the host stamps on source-loading/source-doc (ISource.Id / the log viewer).
const ICONS: Record<string, () => JSX.Element> = {
  notion: () => <NotionIcon size={13} class="editor-tab-icon" />,
  logs: () => <Logs size={13} class="editor-tab-icon" />,
  corrections: () => <GraduationCap size={13} class="editor-tab-icon" />,
};

// The icon for a source tab, from its store entry's source id. No icon when the id is unknown or the entry is
// absent (a restored tab whose content hasn't been repopulated) — better than guessing a brand mark.
export function sourceTabIcon(session: ClientSession | null, target: string): JSX.Element | null {
  return ICONS[sourceDoc(session, target)?.sourceId ?? ""]?.() ?? null;
}
