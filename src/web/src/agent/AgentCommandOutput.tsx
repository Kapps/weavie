import { createSignal, createUniqueId, type JSX, onCleanup, onMount, Show } from "solid-js";
import { setContext } from "../commands/context";
import { liveKeyLabel } from "../commands/keys-live";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";

interface MountedDisclosure {
  element: HTMLDetailsElement;
  toggle: () => void;
  visible: boolean;
}

const disclosures = new Map<string, MountedDisclosure>();
const disclosuresByElement = new WeakMap<HTMLDetailsElement, MountedDisclosure>();

export function AgentCommandOutput(props: { renderOutput: () => JSX.Element }): JSX.Element {
  let details: HTMLDetailsElement | undefined;
  const outputId = `agent-command-output-${createUniqueId()}`;
  const [expanded, setExpanded] = createSignal(false);
  const toggle = (): void => {
    setExpanded((current) => !current);
  };
  const title = (): string => {
    const action = expanded() ? "Hide command output" : "Show command output";
    const key = liveKeyLabel(CommandIds.toggleAgentCommandOutput);
    return key === "" ? action : `${action} (${key})`;
  };

  onMount(() => {
    const element = details!;
    const disclosure = { element, toggle, visible: false };
    disclosures.set(outputId, disclosure);
    disclosuresByElement.set(element, disclosure);
    const viewport = element.closest(".agent-body");
    disclosure.visible = viewport !== null && intersects(element, viewport);
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
      class="agent-command-output-details"
      data-agent-command-output
      open={expanded()}
      ref={details}
    >
      {/* biome-ignore lint/a11y/noStaticElementInteractions: summary is the native details control. */}
      <summary
        aria-controls={outputId}
        title={title()}
        onClick={(event) => {
          event.preventDefault();
          void runCommandWithFeedback(CommandIds.toggleAgentCommandOutput, { outputId });
        }}
      >
        {expanded() ? "hide output" : "show output"}
      </summary>
      <Show when={expanded()} keyed>
        {(_expanded) => (
          <div class="agent-command-output" id={outputId}>
            {props.renderOutput()}
          </div>
        )}
      </Show>
    </details>
  );
}

export function toggleAgentCommandOutput(args: unknown): boolean {
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
  const details = document.activeElement?.closest<HTMLDetailsElement>(
    "[data-agent-command-output]",
  );
  return details === null || details === undefined ? undefined : disclosuresByElement.get(details);
}

function newestActiveDisclosure(): MountedDisclosure | undefined {
  const body = document.querySelector<HTMLElement>(".agent-surface.active .agent-body");
  if (body === null) {
    return undefined;
  }
  const elements = document.querySelectorAll<HTMLDetailsElement>(
    ".agent-surface.active [data-agent-command-output]",
  );
  for (let index = elements.length - 1; index >= 0; index -= 1) {
    const details = elements.item(index);
    const disclosure = disclosuresByElement.get(details);
    if (disclosure !== undefined && intersects(details, body)) {
      return disclosure;
    }
  }
  return undefined;
}

function intersects(element: Element, viewport: Element): boolean {
  const bounds = element.getBoundingClientRect();
  const visible = viewport.getBoundingClientRect();
  return bounds.bottom > visible.top && bounds.top < visible.bottom;
}

function publishAvailability(): void {
  const active = document.querySelector(".agent-surface.active");
  setContext(
    "agentCommandOutputAvailable",
    active !== null &&
      [...disclosures.values()].some(
        (disclosure) => disclosure.visible && active.contains(disclosure.element),
      ),
  );
}
