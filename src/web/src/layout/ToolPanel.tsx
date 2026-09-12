import { Pin, PinOff, X } from "lucide-solid";
import { createEffect, createMemo, type JSX, on, onCleanup, Show } from "solid-js";
import { registerFloatingPanel } from "../chrome/floating-panels";
import { liveKeyHint } from "../commands/keys-live";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import type { ToolPanels } from "./tool-panels";
import type { ToolKind } from "./types";

export function ToolPanel(props: {
  kind: ToolKind;
  title: string;
  panels: ToolPanels;
  children: JSX.Element;
}): JSX.Element {
  let root!: HTMLDivElement;
  let floating: ReturnType<typeof registerFloatingPanel> | undefined;
  const dockCommand = (): string =>
    props.kind === "files" ? CommandIds.dockFileBrowser : CommandIds.dockSearch;
  const closeCommand = (): string =>
    props.panels.floating(props.kind) ? CommandIds.closeFloatingPanel : CommandIds.closeToolPanel;
  const close = (): void => {
    void props.panels.close(props.kind).catch((error: unknown) => notify("warn", String(error)));
  };
  createEffect(() => {
    if (!props.panels.floating(props.kind)) return;
    floating = registerFloatingPanel(props.kind, close, "tool");
    onCleanup(() => {
      floating?.dispose();
      floating = undefined;
    });
  });
  const visible = createMemo(() => props.panels.visible(props.kind));
  createEffect(
    on([props.panels.focusRequest, visible], ([request, showing]) => {
      if (request?.kind === props.kind && showing) {
        (
          root.querySelector<HTMLElement>(".search-input") ??
          root.querySelector<HTMLElement>(".browser-row.active") ??
          root.querySelector<HTMLElement>(".tool-close")
        )?.focus();
      }
    }),
  );
  return (
    <div
      ref={root}
      class="tool-panel"
      data-tool={props.kind}
      classList={{ "tool-floating": props.panels.floating(props.kind) }}
      onPointerDown={() => floating?.raise()}
      onFocusIn={() => floating?.raise()}
    >
      <div class="tool-head">
        <span class="tool-title" title={props.title}>
          {props.title}
        </span>
        <button
          type="button"
          class="tool-pin"
          aria-pressed={props.panels.docked(props.kind)}
          title={`${props.panels.docked(props.kind) ? "Float" : "Stay Open"}${liveKeyHint(dockCommand())}`}
          onClick={() => void runCommandWithFeedback(dockCommand())}
        >
          <Show when={props.panels.docked(props.kind)} fallback={<Pin />}>
            <PinOff />
          </Show>
        </button>
        <button
          type="button"
          class="tool-close"
          title={`Close${liveKeyHint(closeCommand())}`}
          onClick={close}
        >
          <X />
        </button>
      </div>
      {props.children}
    </div>
  );
}
