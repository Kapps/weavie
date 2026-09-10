import { normalizePath, samePath } from "./fs-path";
import type { EditorSessionEntry } from "./session-types";

export const REVIEW_TAB_KEY = "weavie:review";
export type TabKind = NonNullable<EditorSessionEntry["kind"]>;
export const tabKind = (entry: Pick<EditorSessionEntry, "kind">): TabKind => entry.kind ?? "file";
export const isFileTab = (entry: Pick<EditorSessionEntry, "kind">): boolean =>
  tabKind(entry) === "file";
export const matchesTab = (entry: EditorSessionEntry, path: string): boolean =>
  isFileTab(entry) ? samePath(entry.path, path) : entry.path === path;
export const tabResourceKey = (entry: EditorSessionEntry): string =>
  `${tabKind(entry)}\0${isFileTab(entry) ? normalizePath(entry.path) : entry.path}`;
