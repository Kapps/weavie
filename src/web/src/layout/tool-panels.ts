import { type Accessor, createMemo, createSignal } from "solid-js";
import { dismissFloatingPopovers } from "../chrome/floating-panels";
import { changeTool } from "./store";
import { type LayoutNode, TOOL_KINDS, type ToolKind } from "./types";

export function toolPane(
  root: LayoutNode,
  kind: ToolKind,
): Extract<LayoutNode, { type: "pane" }> | undefined {
  if (root.type === "pane") return root.kind === kind ? root : undefined;
  for (const child of root.children) {
    const pane = toolPane(child, kind);
    if (pane !== undefined) return pane;
  }
  return undefined;
}

export function createToolPanels(options: {
  backendId: Accessor<string>;
  root: Accessor<LayoutNode>;
  compact: Accessor<boolean>;
  revealDock: () => void;
  restoreFocus: () => void;
}) {
  const [requests, setRequests] = createSignal(new Map<string, ToolKind[]>());
  const [requestedFocus, setFocusRequest] = createSignal<{
    backendId: string;
    kind: ToolKind;
    nonce: number;
  } | null>(null);
  const current = createMemo(() => requests().get(options.backendId()) ?? []);
  const focusRequest = createMemo(() => {
    const request = requestedFocus();
    return request?.backendId === options.backendId() ? request : null;
  });
  const states = new Map(
    TOOL_KINDS.map((kind) => {
      const pane = createMemo(() => toolPane(options.root(), kind));
      const docked = createMemo(() => pane() !== undefined);
      const visible = createMemo(() =>
        options.compact() || !docked() ? current().includes(kind) : pane()?.hidden !== true,
      );
      const floating = createMemo(() => visible() && (options.compact() || !docked()));
      return [kind, { docked, visible, floating }] as const;
    }),
  );
  const docked = (kind: ToolKind): boolean => states.get(kind)!.docked();
  const visible = (kind: ToolKind): boolean => states.get(kind)!.visible();
  const floating = (kind: ToolKind): boolean => states.get(kind)!.floating();
  const request = (backendId: string, kind: ToolKind, open: boolean): void => {
    setRequests((all) => {
      const next = (all.get(backendId) ?? []).filter((item) => item !== kind);
      if (open) next.push(kind);
      return new Map(all).set(backendId, next);
    });
  };
  const focus = (kind: ToolKind): void => {
    setFocusRequest((previous) => ({
      backendId: options.backendId(),
      kind,
      nonce: (previous?.nonce ?? 0) + 1,
    }));
  };
  const open = async (kind: ToolKind): Promise<void> => {
    dismissFloatingPopovers();
    const backendId = options.backendId();
    request(backendId, kind, true);
    if (docked(kind) && !options.compact()) {
      options.revealDock();
      await changeTool(backendId, kind, "show");
    }
    if (backendId === options.backendId()) focus(kind);
  };
  const close = async (kind: ToolKind): Promise<void> => {
    const backendId = options.backendId();
    if (docked(kind) && !options.compact()) await changeTool(backendId, kind, "hide");
    request(backendId, kind, false);
    if (backendId === options.backendId()) options.restoreFocus();
  };
  const toggleDock = async (kind: ToolKind): Promise<void> => {
    dismissFloatingPopovers();
    const backendId = options.backendId();
    const action = docked(kind) ? "float" : "dock";
    request(backendId, kind, true);
    options.revealDock();
    await changeTool(backendId, kind, action);
    if (backendId === options.backendId()) focus(kind);
  };
  return { docked, visible, floating, focusRequest, open, close, toggleDock };
}

export type ToolPanels = ReturnType<typeof createToolPanels>;
