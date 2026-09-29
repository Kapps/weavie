import type { ClientSession } from "../bridge";

export type FileReferenceResolution =
  | { kind: "file"; path: string; line: number | null }
  | { kind: "ambiguous"; query: string; line: number | null }
  | { kind: "missing"; message: string };

export interface FileNavigation {
  reveal(path: string, line: number | undefined, preview: boolean): Promise<void>;
  openFiles(paths: readonly string[]): Promise<void>;
}

const owners = new WeakMap<ClientSession, FileNavigation>();

/** The exact session's controller owns file navigation, including asynchronous reference resolution. */
export function ownFileNavigation(session: ClientSession, navigation: FileNavigation): () => void {
  if (owners.has(session)) throw new Error("The session already owns file reveals.");
  owners.set(session, navigation);
  return () => {
    if (owners.get(session) === navigation) owners.delete(session);
  };
}

function navigationFor(session: ClientSession): FileNavigation {
  const navigation = owners.get(session);
  if (navigation === undefined) throw new Error("The session no longer owns file reveals.");
  return navigation;
}

/** An omitted line preserves the reading position of an already-open tab. */
export function revealFileIn(
  session: ClientSession | null,
  path: string,
  line: number | undefined,
  preview: boolean,
): Promise<void> {
  if (session === null) return Promise.resolve();
  return navigationFor(session).reveal(path, line, preview);
}

/** OS-delivered opens retain every file, independently of later reference navigation. */
export function openFilesIn(session: ClientSession, paths: readonly string[]): Promise<void> {
  return navigationFor(session).openFiles(paths);
}
