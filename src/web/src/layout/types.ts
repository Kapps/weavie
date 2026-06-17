// Layout document mirrored from Weavie.Core.Layout, as it crosses the bridge (camelCase JSON, enums
// as strings). Kept structural and lenient — the host is the source of truth and validates.

export type SplitDir = "row" | "column";

export type LayoutNode =
  | { type: "split"; dir: SplitDir; weights: number[]; children: LayoutNode[] }
  | { type: "pane"; id: string; kind: string };

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
