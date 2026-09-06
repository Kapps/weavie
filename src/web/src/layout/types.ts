// Layout document mirrored from Weavie.Core.Layout as it crosses the bridge (camelCase JSON, enums as
// strings). Structural and lenient: the host is the source of truth and validates.

export type SplitDir = "row" | "column";

export type LayoutNode =
  | { type: "split"; dir: SplitDir; weights: number[]; children: LayoutNode[] }
  | { type: "pane"; id: string; kind: string; hidden?: boolean };

export type ToolKind = "files" | "search";
export const TOOL_KINDS: ToolKind[] = ["files", "search"];
export const PANE_KINDS = ["terminal:claude", "terminal:shell", "editor", ...TOOL_KINDS];

export function isTool(kind: string): kind is ToolKind {
  return TOOL_KINDS.some((tool) => tool === kind);
}

export interface WindowState {
  x: number;
  y: number;
  width: number;
  height: number;
  maximized: boolean;
}

export interface LayoutDocument {
  version: number;
  seenPaneLevel: number;
  focused?: string;
  dismissed: string[];
  window?: WindowState;
  root: LayoutNode;
}
