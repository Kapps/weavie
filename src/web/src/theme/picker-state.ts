import { createSignal } from "solid-js";
import { hostConnection, LOCAL_BACKEND_ID, type ThemeSlot } from "../bridge";
import { registerCommand } from "../commands/registry";
import { CommandIds, type CommandResult } from "../commands/types";
import { currentThemeType, type ThemeTarget } from "./controller";

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

/** Sets the editor theme for the current polarity; an empty id resets it to follow the app theme. */
export async function selectEditorTheme(id: string, signal: AbortSignal): Promise<void> {
  const polarity = currentThemeType();
  const result = await themeRequest<CommandResult>("selectEditor", { id, polarity }, signal);
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

// The open picker's target theme and starting catalog; null while it's closed.
const [picker, setPicker] = createSignal<{
  target: ThemeTarget;
  source: "installed" | "registry";
} | null>(null);
export const themePickerOpen = (): boolean => picker() !== null;
export const themePickerTarget = (): ThemeTarget => picker()?.target ?? "app";
export const themePickerStartsInRegistry = (): boolean => picker()?.source === "registry";
export const closeThemePicker = (): void => {
  setPicker(null);
};
/** Opens the picker on the Open VSX catalog rather than the installed themes. */
export const openThemeRegistry = (): void => {
  setPicker({ target: "app", source: "registry" });
};
registerCommand(CommandIds.selectTheme, async (args) => {
  const id = (args as { id?: unknown } | undefined)?.id;
  if (id !== undefined) {
    if (typeof id !== "string" || id.length === 0)
      throw new Error("Theme id must be a nonempty string.");
    await selectTheme(id, new AbortController().signal);
  } else {
    setPicker({ target: "app", source: "installed" });
  }
});
registerCommand(CommandIds.selectEditorTheme, async (args) => {
  const id = (args as { id?: unknown } | undefined)?.id;
  if (id !== undefined) {
    if (typeof id !== "string") throw new Error("Theme id must be a string.");
    await selectEditorTheme(id, new AbortController().signal);
  } else {
    setPicker({ target: "editor", source: "installed" });
  }
});
