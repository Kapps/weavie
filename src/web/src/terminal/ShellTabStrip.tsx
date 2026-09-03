import { Plus, X } from "lucide-solid";
import { createEffect, For, type JSX } from "solid-js";
import { liveKeyHint } from "../commands/keys-live";
import { CommandIds } from "../commands/types";

export function ShellTabStrip(props: {
  terminals: () => string[];
  activeId: () => string | null;
  title: (id: string, index: number) => string;
  trailing: JSX.Element;
  onSelect: (id: string) => void;
  onClose: (id: string) => void;
  onNew: () => void;
}): JSX.Element {
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
    <div class="shell-tabs pane-tabs" role="tablist" aria-label="Shell terminals">
      <div class="shell-tabs-track pane-tabs-track" ref={track}>
        <For each={props.terminals()}>
          {(id, index) => (
            <div class="shell-tab pane-tab" classList={{ active: props.activeId() === id }}>
              <button
                type="button"
                class="shell-tab-main pane-tab-main"
                role="tab"
                aria-selected={props.activeId() === id}
                title={props.title(id, index())}
                data-middle-click="close"
                onClick={() => props.onSelect(id)}
                onMouseDown={(event) => {
                  if (event.button === 1) {
                    event.preventDefault();
                    props.onClose(id);
                  }
                }}
              >
                <span class="shell-tab-label pane-tab-label">{props.title(id, index())}</span>
              </button>
              <button
                type="button"
                class="shell-tab-close pane-tab-close"
                aria-label="Close terminal"
                title={`Close terminal${liveKeyHint(CommandIds.closeTerminalPrompt)}`}
                onClick={() => props.onClose(id)}
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
        title={`New terminal${liveKeyHint(CommandIds.newTerminal)}`}
        onClick={() => props.onNew()}
      >
        <Plus size={14} />
      </button>
      {props.trailing}
    </div>
  );
}
