import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

function sessionDocuments(home: string): string[] {
  const root = join(home, ".weavie", "workspaces");
  try {
    return readdirSync(root).flatMap((id) => {
      try {
        return [readFileSync(join(root, id, "sessions.json"), "utf8")];
      } catch {
        return [];
      }
    });
  } catch {
    return [];
  }
}

/** Every workspace's persisted session overlay, or an empty string until the host writes one. */
export function persistedSessions(home: string): string {
  return sessionDocuments(home).join("\n");
}

export function persistedActiveTabs(home: string): string[] {
  return sessionDocuments(home).flatMap((serialized) => {
    const document: { sessions: { editorSession?: { active?: string } }[] } =
      JSON.parse(serialized);
    return document.sessions.flatMap((session) => session.editorSession?.active ?? []);
  });
}
