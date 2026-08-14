import { type JSX, Show } from "solid-js";
import { agentProviders } from "../chrome/agent-default";
import { AgentPaneBody } from "./AgentPaneBody";
import { AgentStatusLine } from "./AgentStatusLine";
import { AgentWorkingStatus } from "./AgentWorkingStatus";
import type { AgentPaneModel } from "./pane-store";

export function AgentPane(props: {
  inputProtocol: number;
  compact: boolean;
  model: AgentPaneModel;
  providerId: string | null;
  active: boolean;
  shortcut: string;
  onFocus: () => void;
  backendId: string;
}): JSX.Element {
  const providerName = (): string =>
    agentProviders(props.backendId).find((provider) => provider.id === props.providerId)?.name ??
    props.providerId ??
    "Agent";

  const focusPromptIn = (surface: EventTarget | null): void => {
    props.onFocus();
    if (surface instanceof HTMLElement) {
      surface
        .querySelector<HTMLTextAreaElement>("[data-agent-composer] textarea")
        ?.focus({ preventScroll: true });
    }
  };
  const hasTextSelection = (): boolean => document.getSelection()?.isCollapsed === false;

  const focusPrompt = (event: MouseEvent): void => {
    if (event.button !== 0 || event.detail === 0) {
      return;
    }

    const target = event.target;
    if (
      target instanceof Element &&
      target.closest(
        "textarea, input, select, [contenteditable]:not([contenteditable='false'])",
      ) !== null
    ) {
      return;
    }
    if (hasTextSelection()) {
      return;
    }
    focusPromptIn(event.currentTarget);
  };

  const focusPromptFromDisabled = (event: PointerEvent): void => {
    if (
      event.button === 0 &&
      event.target instanceof Element &&
      event.target.closest(":disabled") !== null &&
      !hasTextSelection()
    ) {
      focusPromptIn(event.currentTarget);
    }
  };

  return (
    // biome-ignore lint/a11y/noStaticElementInteractions: Surface clicks are a pointer convenience; the textarea remains keyboard-focusable.
    // biome-ignore lint/a11y/useKeyWithClickEvents: Keyboard activation already keeps the activated control focused.
    <div
      class="agent-surface"
      classList={{ active: props.active }}
      data-kind="terminal:claude"
      data-agent-provider={props.providerId ?? "unknown"}
      data-surface="structured-agent"
      onClick={focusPrompt}
      onPointerUp={focusPromptFromDisabled}
    >
      <div class="pane-head" role="toolbar">
        <span class="pane-label">{providerName()}</span>
        <Show when={props.compact}>
          <AgentWorkingStatus
            compact
            pendingKind={props.model.pendingRequestKind()}
            turnActive={props.model.turnActive()}
            turnStartedAt={props.model.turnStartedAt()}
          />
          <AgentStatusLine compact session={props.model.session} />
        </Show>
        <Show when={props.shortcut !== ""}>
          <span class="pane-shortcut">{props.shortcut}</span>
        </Show>
      </div>
      <AgentPaneBody
        active={props.active}
        compact={props.compact}
        inputProtocol={props.inputProtocol}
        model={props.model}
        providerName={providerName()}
      />
      <Show when={!props.compact}>
        <AgentStatusLine compact={false} session={props.model.session} />
      </Show>
    </div>
  );
}
