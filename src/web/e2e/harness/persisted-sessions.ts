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
