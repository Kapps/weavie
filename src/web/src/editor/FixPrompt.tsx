import { createSignal, type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { ModalShell, modalSubmitKeys, PromptActions, PromptButton } from "../chrome/ModalShell";
import { captureCommandRunnerFor } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { basename } from "./fs-path";

/** A line-anchored item (a review hint or a PR thread) the session's agent can be asked to fix. */
export interface FixItem {
  path: string;
  line: number;
  quote: string;
  text: string;
}

/** Sends the item to the session's agent; an empty instruction is a plain Fix. */
export function fixLine(session: ClientSession, item: FixItem, instruction: string): void {
  void captureCommandRunnerFor(session, new Map())(CommandIds.agentFixLine, {
    ...item,
    instruction,
  });
}

const [request, setRequest] = createSignal<{ session: ClientSession; item: FixItem } | null>(null);

/** Asks for the user's own instruction, then sends the item with it. */
export function fixWith(session: ClientSession, item: FixItem): void {
  setRequest({ session, item });
}

/** The app-level Fix with… prompt; an editor-hosted input can't receive keystrokes. */
export function FixPrompt(): JSX.Element {
  return (
    <Show when={request()} keyed>
      {(current) => {
        let input!: HTMLInputElement;
        const cancel = (): void => {
          setRequest(null);
        };
        const submit = (): void => {
          const instruction = input.value.trim();
          if (instruction === "") return;
          setRequest(null);
          fixLine(current.session, current.item, instruction);
        };
        return (
          <ModalShell
            labelledBy="fix-title"
            onDismiss={cancel}
            onKeyDown={modalSubmitKeys(submit, cancel)}
          >
            <div class="confirm-title" id="fix-title">
              Fix with…
            </div>
            <div class="confirm-body">
              How should the agent fix {basename(current.item.path)}:{current.item.line}?
            </div>
            <input
              class="session-prompt-input"
              type="text"
              spellcheck={false}
              autocomplete="off"
              placeholder="Keep the public signature unchanged"
              ref={(el) => {
                input = el;
                queueMicrotask(() => el.focus());
              }}
            />
            <PromptActions>
              <PromptButton label="Cancel" shortcut="Esc" title="Cancel (Esc)" onClick={cancel} />
              <PromptButton
                label="Fix"
                shortcut="Enter"
                title="Fix (Enter)"
                onClick={submit}
                primary
              />
            </PromptActions>
          </ModalShell>
        );
      }}
    </Show>
  );
}
