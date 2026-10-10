import { For, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyHint } from "../commands/keys-live";
import { registerCommand, runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import {
  type AgentBackgroundItem,
  agentBackground,
  backgroundRunning,
} from "./agent-background-store";
import { backgroundStatus, rovingTarget } from "./background-format";
import { liveNow } from "./live-clock";

/** Everything still running beside the session's turns, as a roving-focus toolbar above the composer. */
export function AgentBackgroundTray(props: {
  session: ClientSession;
  onJump: (item: AgentBackgroundItem) => void;
}): JSX.Element {
  const items = () => agentBackground(props.session);
  const now = liveNow(() => items().some(backgroundRunning));
  let tray: HTMLDivElement | undefined;
  const buttons = (): HTMLButtonElement[] => [...(tray?.querySelectorAll("button") ?? [])];
  const focusAt = (index: number): void => {
    const all = buttons();
    const target = all[index];
    for (const button of all) button.tabIndex = button === target ? 0 : -1;
    target?.focus();
  };
  const onKeyDown = (event: KeyboardEvent): void => {
    const all = buttons();
    const next = rovingTarget(
      event.key,
      all.indexOf(document.activeElement as HTMLButtonElement),
      all.length,
    );
    if (next === null) return;
    event.preventDefault();
    focusAt(next);
  };
  onMount(() =>
    onCleanup(
      registerCommand(CommandIds.showBackgroundWork, (_args, context) => {
        if (context.session !== props.session || buttons().length === 0) return false;
        focusAt(0);
        return true;
      }),
    ),
  );

  return (
    <Show when={items().length > 0}>
      <div
        ref={tray}
        class="agent-status-line agent-background-tray"
        role="toolbar"
        aria-label="Background work"
        onKeyDown={onKeyDown}
      >
        <span class="agent-background-label">Background</span>
        <For each={items()}>
          {(item, index) => (
            <span
              class="agent-background-item"
              data-background-item={item.id}
              data-state={item.state}
            >
              <button
                type="button"
                class="agent-status-segment"
                tabIndex={index() === 0 ? 0 : -1}
                title={`${item.detail ?? item.name} — ${backgroundStatus(item.state, item.startedAtMs, item.endedAtMs, now(), item.usage)}`}
                aria-disabled={item.transcriptItemId === null}
                onClick={() => props.onJump(item)}
              >
                <Show when={backgroundRunning(item)} fallback={<span aria-hidden="true">•</span>}>
                  <span class="agent-working-spinner" aria-hidden="true" />
                </Show>
                <span class="agent-status-value">{item.name}</span>
                <span>
                  {backgroundStatus(item.state, item.startedAtMs, item.endedAtMs, now(), null)}
                </span>
              </button>
              <Show when={item.canStop}>
                <button
                  type="button"
                  class="agent-status-segment agent-background-stop"
                  tabIndex={-1}
                  title={`Stop ${item.name}${liveKeyHint(CommandIds.stopBackgroundTask)}`}
                  onClick={() =>
                    void runCommandWithFeedback(CommandIds.stopBackgroundTask, { id: item.id })
                  }
                >
                  Stop
                </button>
              </Show>
            </span>
          )}
        </For>
      </div>
    </Show>
  );
}
