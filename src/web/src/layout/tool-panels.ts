import { type Accessor, createMemo, createSignal } from "solid-js";
import type { FocusIntent } from "../chrome/deferred-focus";
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
  captureFocus: () => FocusIntent;
}) {
  const [requests, setRequests] = createSignal(new Map<string, ToolKind[]>());
  const [requestedFocus, setFocusRequest] = createSignal<{
    backendId: string;
    kind: ToolKind;
    intent: FocusIntent;
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
  const focus = (kind: ToolKind, intent: FocusIntent): void => {
    setFocusRequest({
      backendId: options.backendId(),
      kind,
      intent,
    });
  };
  const open = async (kind: ToolKind): Promise<void> => {
    dismissFloatingPopovers();
    const backendId = options.backendId();
    const needsDock = docked(kind) && !options.compact();
    if (needsDock) options.revealDock();
    const intent = options.captureFocus();
    request(backendId, kind, true);
    if (needsDock) {
      await changeTool(backendId, kind, "show").catch((error: unknown) => {
        intent.dispose();
        throw error;
      });
    }
    if (backendId === options.backendId()) focus(kind, intent);
    else intent.dispose();
  };
  const close = async (kind: ToolKind): Promise<void> => {
    const intent = options.captureFocus();
    const backendId = options.backendId();
    try {
      if (docked(kind) && !options.compact()) await changeTool(backendId, kind, "hide");
      request(backendId, kind, false);
      if (backendId === options.backendId()) intent.complete(options.restoreFocus);
    } finally {
      intent.dispose();
    }
  };
  const toggleDock = async (kind: ToolKind): Promise<void> => {
    dismissFloatingPopovers();
    const backendId = options.backendId();
    const action = docked(kind) ? "float" : "dock";
    request(backendId, kind, true);
    options.revealDock();
    const intent = options.captureFocus();
    await changeTool(backendId, kind, action).catch((error: unknown) => {
      intent.dispose();
      throw error;
    });
    if (backendId === options.backendId()) focus(kind, intent);
    else intent.dispose();
  };
  return { docked, visible, floating, focusRequest, open, close, toggleDock };
}

export type ToolPanels = ReturnType<typeof createToolPanels>;
