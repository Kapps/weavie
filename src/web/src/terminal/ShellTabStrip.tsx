import { Plus, X } from "lucide-solid";
import { createEffect, createSignal, For, type JSX, onCleanup } from "solid-js";
import { keyHint } from "../commands/key-hint";
import { onCommandsChanged } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { ShellTerminalDescriptor } from "./shell-terminal-store";

export function ShellTabStrip(props: {
  terminals: () => ShellTerminalDescriptor[];
  activeId: () => string | null;
  title: (terminal: ShellTerminalDescriptor, index: number) => string;
  trailing: JSX.Element;
  onSelect: (id: string) => void;
  onClose: (id: string) => void;
  onNew: () => void;
}): JSX.Element {
  const [catalogVersion, setCatalogVersion] = createSignal(0);
  onCleanup(onCommandsChanged(() => setCatalogVersion((version) => version + 1)));
  const commandTitle = (label: string, commandId: string): string => {
    void catalogVersion();
    return label + keyHint(commandId);
  };
  let track!: HTMLDivElement;
  createEffect(() => {
    void props.activeId();
    queueMicrotask(() =>
      track.querySelector<HTMLElement>(".shell-tab.active")?.scrollIntoView({
        block: "nearest",
        inline: "nearest",
      }),
    );
  });

  return (
    <div class="shell-tabs" role="tablist" aria-label="Shell terminals">
      <div class="shell-tabs-track" ref={track}>
        <For each={props.terminals()}>
          {(terminal, index) => (
            <div class="shell-tab" classList={{ active: props.activeId() === terminal.id }}>
              <button
                type="button"
                class="shell-tab-main"
                role="tab"
                aria-selected={props.activeId() === terminal.id}
                title={props.title(terminal, index())}
                data-middle-click="close"
                onClick={() => props.onSelect(terminal.id)}
                onMouseDown={(event) => {
                  if (event.button === 1) {
                    event.preventDefault();
                    props.onClose(terminal.id);
                  }
                }}
              >
                <span class="shell-tab-label">{props.title(terminal, index())}</span>
              </button>
              <button
                type="button"
                class="shell-tab-close"
                aria-label="Close terminal"
                title={commandTitle("Close terminal", CommandIds.closeTerminalPrompt)}
                onClick={() => props.onClose(terminal.id)}
              >
                <X size={13} />
              </button>
            </div>
          )}
        </For>
      </div>
      <button
        type="button"
        class="shell-tab-new"
        aria-label="New terminal"
        title={commandTitle("New terminal", CommandIds.newTerminal)}
        onClick={() => props.onNew()}
      >
        <Plus size={14} />
      </button>
      {props.trailing}
    </div>
  );
}
