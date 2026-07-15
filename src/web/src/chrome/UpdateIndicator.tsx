import { createEffect, createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { keyHint } from "../commands/key-hint";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { type UpdateHold, updateHolds, updatePending, updateRestarting } from "./update-store";

const holdReasonText: Record<UpdateHold["reason"], string> = {
  working: "Claude is working",
  "needs-input": "Claude awaits input",
  "shell-job": "shell job running",
  "waiting-on-task": "waiting on a scheduled task",
};
const holdText = (hold: UpdateHold): string => `${hold.session}: ${holdReasonText[hold.reason]}`;

/**
 * The pending-update indicator, at home in the status footer beside the branch: a compact
 * "⟳ Update ready" segment that opens a card (a portalled popover anchored above it, so the footer's
 * `overflow: hidden` can't clip it) listing what holds the update plus the explicit Restart Now. Present
 * only while an update is pending; hidden once the restart commits (the blocking overlay takes over).
 * Visibility keys on `updatePending` (steady) rather than `updateHolds` so a mid-drain reconnect's
 * transient holds clear doesn't flicker the chip.
 */
export function UpdateIndicator(): JSX.Element {
  const [open, setOpen] = createSignal(false);
  // The chip's bottom-right in viewport coords, captured on open; the card anchors its own bottom-right there.
  const [anchor, setAnchor] = createSignal<{ right: number; bottom: number } | null>(null);
  // Close when the episode genuinely ends (not on a reconnect's transient clear — see `updatePending`).
  createEffect(() => {
    if (!updatePending()) {
      setOpen(false);
    }
  });

  const toggle = (event: MouseEvent & { currentTarget: HTMLElement }): void => {
    const rect = event.currentTarget.getBoundingClientRect();
    setAnchor({ right: window.innerWidth - rect.right, bottom: window.innerHeight - rect.top + 6 });
    setOpen((v) => !v);
  };
  const onPointerDown = (event: PointerEvent): void => {
    if (!(event.target as HTMLElement).closest(".update-card, .update-chip")) {
      setOpen(false);
    }
  };
  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Escape") {
      setOpen(false);
    }
  };
  onMount(() => {
    window.addEventListener("pointerdown", onPointerDown);
    window.addEventListener("keydown", onKeyDown);
  });
  onCleanup(() => {
    window.removeEventListener("pointerdown", onPointerDown);
    window.removeEventListener("keydown", onKeyDown);
  });

  return (
    <Show when={!updateRestarting() && updatePending()}>
      <button
        type="button"
        class="footer-seg update-chip"
        classList={{ "update-chip-open": open() }}
        title="Update ready — applies when your sessions go idle. Click for details."
        onClick={toggle}
      >
        <span class="update-chip-glyph" aria-hidden="true">
          ⟳
        </span>
        Update ready
      </button>
      <Show when={open() && anchor()} keyed>
        {(pos) => (
          <Portal>
            <output
              class="update-card"
              ref={(el) => {
                el.style.right = `${pos.right}px`;
                el.style.bottom = `${pos.bottom}px`;
              }}
            >
              <div class="update-card-head">
                <span class="update-card-title">Update ready</span>
                <button
                  type="button"
                  class="update-card-collapse"
                  title="Collapse"
                  aria-label="Collapse update details"
                  onClick={() => setOpen(false)}
                >
                  –
                </button>
              </div>
              <span class="update-card-body">
                Applies on its own once your sessions go idle. Restarting now reloads every session
                (conversations are kept) and ends background shell jobs.
              </span>
              <ul class="update-card-holds">
                <For each={updateHolds() ?? []}>{(hold) => <li>{holdText(hold)}</li>}</For>
              </ul>
              <button
                type="button"
                class="update-card-restart"
                title={`Restart now to apply the update${keyHint(CommandIds.restartForUpdate)}`}
                onClick={() => void runCommandWithFeedback(CommandIds.restartForUpdate)}
              >
                Restart Now{keyHint(CommandIds.restartForUpdate)}
              </button>
            </output>
          </Portal>
        )}
      </Show>
    </Show>
  );
}
