import { createSignal, createUniqueId, type JSX, Show } from "solid-js";
import { Portal } from "solid-js/web";
import type { ClientSession } from "../bridge";
import { contextUsedPercent, formatTokenCount } from "./AgentUsageFormat";
import { agentContextUsage } from "./agent-usage-store";

export function AgentUsageIndicator(props: { session: ClientSession | null }): JSX.Element {
  const tooltipId = `agent-usage-${createUniqueId()}`;
  const [anchor, setAnchor] = createSignal<DOMRect | null>(null);
  let tooltip: HTMLDivElement | undefined;
  const open = (element: HTMLElement): void => {
    const bounds = element.getBoundingClientRect();
    setAnchor(bounds);
    queueMicrotask(() => tooltip !== undefined && placeTooltip(tooltip, bounds));
  };

  return (
    <Show when={agentContextUsage(props.session)}>
      {(context) => {
        const used = () => contextUsedPercent(context());
        return (
          <button
            type="button"
            class="agent-status-usage"
            aria-label={`Context window ${used()}% used`}
            aria-describedby={tooltipId}
            onPointerEnter={(event) => open(event.currentTarget)}
            onPointerLeave={() => setAnchor(null)}
            onFocus={(event) => open(event.currentTarget)}
            onBlur={() => setAnchor(null)}
            onClick={(event) => event.stopPropagation()}
            onKeyDown={(event) => {
              if (event.key === "Escape") setAnchor(null);
            }}
          >
            <span
              class="agent-usage-circle"
              style={`--agent-usage-percent: ${used()}%`}
              aria-hidden="true"
            />
            <Show when={anchor() !== null}>
              <Portal>
                <div id={tooltipId} ref={tooltip} class="agent-usage-tooltip" role="tooltip">
                  <div class="agent-usage-tooltip-section">
                    <span class="agent-usage-tooltip-title">Context window</span>
                    <span class="agent-usage-tooltip-value">{used()}% used</span>
                    <span class="agent-usage-tooltip-detail">
                      {formatTokenCount(context().usedTokens)} of{" "}
                      {formatTokenCount(context().capacityTokens)} tokens
                    </span>
                  </div>
                </div>
              </Portal>
            </Show>
          </button>
        );
      }}
    </Show>
  );
}

function placeTooltip(tooltip: HTMLDivElement, anchor: DOMRect): void {
  const margin = 8;
  tooltip.style.left = `${Math.max(
    margin,
    Math.min(anchor.left, window.innerWidth - tooltip.offsetWidth - margin),
  )}px`;
  const above = anchor.top - tooltip.offsetHeight - 6;
  tooltip.style.top = `${above >= margin ? above : anchor.bottom + 6}px`;
}
