import { createSignal } from "solid-js";
import { hostConnection, LOCAL_BACKEND_ID, type ThemeSlot } from "../bridge";
import { registerCommand } from "../commands/registry";
import type { CommandResult } from "../commands/types";

export const SELECT_THEME = "weavie.theme.select";
export interface ThemeChoice {
  id: string;
  label: string;
  type: "light" | "dark";
  namespace: string | null;
  name: string | null;
  version: string | null;
}
export interface ThemePreview {
  choice: ThemeChoice;
  slot: ThemeSlot;
}
export interface ExtensionChoice {
  namespace: string;
  name: string;
  version: string;
  displayName?: string;
  description: string;
  downloadCount: number;
  averageRating?: number;
  reviewCount?: number;
}
export type ThemeSearchOrder = "downloadCount" | "relevance";
export interface SearchResults {
  extensions: ExtensionChoice[];
  offset: number;
  totalSize: number;
}

export function themeRequest<T>(name: string, args: object, signal: AbortSignal): Promise<T> {
  const connection = hostConnection(LOCAL_BACKEND_ID);
  if (connection === undefined) return Promise.reject(new Error("Theme host is disconnected."));
  return connection.host.feature("themes").request<T, object>(name, args, signal);
}

export async function selectTheme(id: string, signal: AbortSignal): Promise<void> {
  const result = await themeRequest<CommandResult>("select", { id }, signal);
  if (!result.ok) throw new Error(result.error);
}

export async function installTheme(choice: ThemeChoice): Promise<void> {
  const result = await themeRequest<CommandResult>(
    "install",
    { namespace: choice.namespace, name: choice.name, version: choice.version },
    new AbortController().signal,
  );
  if (!result.ok) throw new Error(result.error);
}

// Which catalog the open picker starts on; null while it's closed.
const [pickerSource, setPickerSource] = createSignal<"installed" | "registry" | null>(null);
export const themePickerOpen = (): boolean => pickerSource() !== null;
export const themePickerStartsInRegistry = (): boolean => pickerSource() === "registry";
export const setThemePickerOpen = (open: boolean): void => {
  setPickerSource(open ? "installed" : null);
};
/** Opens the picker on the Open VSX catalog rather than the installed themes. */
export const openThemeRegistry = (): void => {
  setPickerSource("registry");
};
registerCommand(SELECT_THEME, async (args) => {
  const id = (args as { id?: unknown } | undefined)?.id;
  if (id !== undefined) {
    if (typeof id !== "string" || id.length === 0)
      throw new Error("Theme id must be a nonempty string.");
    await selectTheme(id, new AbortController().signal);
  } else {
    setThemePickerOpen(true);
  }
});
