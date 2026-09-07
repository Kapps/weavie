import { createSignal, createUniqueId, type JSX, onCleanup, onMount, Show } from "solid-js";
import { setContext } from "../commands/context";
import { liveKeyLabel } from "../commands/keys-live";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { intersectsViewport, newestVisibleAgentElement } from "./AgentViewport";

interface MountedDisclosure {
  element: HTMLDetailsElement;
  toggle: () => void;
  visible: boolean;
}

const disclosures = new Map<string, MountedDisclosure>();
const disclosuresByElement = new WeakMap<HTMLDetailsElement, MountedDisclosure>();

export function AgentToolOutput(props: {
  renderOutput: () => JSX.Element;
  startExpanded: boolean;
}): JSX.Element {
  let details: HTMLDetailsElement | undefined;
  const outputId = `agent-tool-output-${createUniqueId()}`;
  const [expanded, setExpanded] = createSignal(props.startExpanded);
  const toggle = (): void => {
    setExpanded((current) => !current);
  };
  const title = (): string => {
    const action = expanded() ? "Hide tool output" : "Show tool output";
    const key = liveKeyLabel(CommandIds.toggleAgentToolOutput);
    return key === "" ? action : `${action} (${key})`;
  };

  onMount(() => {
    const element = details!;
    const disclosure = { element, toggle, visible: false };
    disclosures.set(outputId, disclosure);
    disclosuresByElement.set(element, disclosure);
    const viewport = element.closest(".agent-body");
    disclosure.visible = viewport !== null && intersectsViewport(element, viewport);
    publishAvailability();
    const observer = new IntersectionObserver(
      ([entry]) => {
        disclosure.visible = entry?.isIntersecting === true;
        publishAvailability();
      },
      { root: viewport },
    );
    observer.observe(element);
    onCleanup(() => {
      observer.disconnect();
      disclosures.delete(outputId);
      disclosuresByElement.delete(element);
      publishAvailability();
    });
  });

  return (
    <details
      class="agent-disclosure agent-tool-output-details"
      data-agent-tool-output
      open={expanded()}
      ref={details}
    >
      {/* biome-ignore lint/a11y/noStaticElementInteractions: summary is the native details control. */}
      <summary
        aria-controls={outputId}
        title={title()}
        onClick={(event) => {
          event.preventDefault();
          void runCommandWithFeedback(CommandIds.toggleAgentToolOutput, { outputId });
        }}
      >
        {expanded() ? "hide output" : "show output"}
      </summary>
      <Show when={expanded()} keyed>
        {(_expanded) => (
          <div class="agent-tool-output" id={outputId}>
            {props.renderOutput()}
          </div>
        )}
      </Show>
    </details>
  );
}

export function toggleAgentToolOutput(args: unknown): boolean {
  const requested = (args as { outputId?: unknown } | undefined)?.outputId;
  if (requested !== undefined && typeof requested !== "string") {
    return false;
  }
  const disclosure =
    typeof requested === "string"
      ? disclosures.get(requested)
      : (focusedDisclosure() ?? newestActiveDisclosure());
  if (disclosure === undefined || !disclosure.element.isConnected) {
    return false;
  }
  disclosure.toggle();
  return true;
}

function focusedDisclosure(): MountedDisclosure | undefined {
  const details = document.activeElement?.closest<HTMLDetailsElement>("[data-agent-tool-output]");
  return details === null || details === undefined ? undefined : disclosuresByElement.get(details);
}

function newestActiveDisclosure(): MountedDisclosure | undefined {
  const element = newestVisibleAgentElement<HTMLDetailsElement>("[data-agent-tool-output]");
  return element === undefined ? undefined : disclosuresByElement.get(element);
}

function publishAvailability(): void {
  const active = document.querySelector(".agent-surface.active");
  setContext(
    "agentToolOutputAvailable",
    active !== null &&
      [...disclosures.values()].some(
        (disclosure) => disclosure.visible && active.contains(disclosure.element),
      ),
  );
}
