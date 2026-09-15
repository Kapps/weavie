import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

/** Every workspace's persisted session overlay, or an empty string until the host writes one. */
export function persistedSessions(home: string): string {
  const root = join(home, ".weavie", "workspaces");
  try {
    return readdirSync(root)
      .map((id) => {
        try {
          return readFileSync(join(root, id, "sessions.json"), "utf8");
        } catch {
          return "";
        }
      })
      .join("\n");
  } catch {
    return "";
  }
}

/**
 * The `active` tab path recorded against every session slot across every workspace's persisted overlay.
 * A tab's path can appear in `open` well before a later `active` correction (debounced separately) lands, so
 * asserting on this — not on raw file content — is what actually proves which tab a reload will restore to.
 */
export function persistedActiveTabs(home: string): string[] {
  const root = join(home, ".weavie", "workspaces");
  let ids: string[];
  try {
    ids = readdirSync(root);
  } catch {
    return [];
  }
  return ids.flatMap((id) => {
    try {
      const document: { sessions?: { editorSession?: { active?: string | null } }[] } = JSON.parse(
        readFileSync(join(root, id, "sessions.json"), "utf8"),
      );
      return (document.sessions ?? [])
        .map((session) => session.editorSession?.active)
        .filter((active): active is string => typeof active === "string");
    } catch {
      return [];
    }
  });
}
