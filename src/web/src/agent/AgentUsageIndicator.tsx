import { createSignal, createUniqueId, For, type JSX, Show } from "solid-js";
import { Portal } from "solid-js/web";
import type { ClientSession } from "../bridge";
import {
  contextUsedPercent,
  formatRateLimitLabel,
  formatResetTime,
  formatTokenCount,
  formatUsedPercent,
} from "./AgentUsageFormat";
import { agentUsageState } from "./agent-usage-store";

export function AgentUsageIndicator(props: { session: ClientSession | null }): JSX.Element {
  const state = () => agentUsageState(props.session);
  const tooltipId = `agent-usage-${createUniqueId()}`;
  const [anchor, setAnchor] = createSignal<DOMRect | null>(null);
  let tooltip: HTMLDivElement | undefined;
  const open = (element: HTMLElement): void => {
    const bounds = element.getBoundingClientRect();
    setAnchor(bounds);
    queueMicrotask(() => tooltip !== undefined && placeTooltip(tooltip, bounds));
  };

  return (
    <Show when={state()}>
      {(usage) => (
        <Show when={usage().contextWindow}>
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
                      <UsageRow
                        title="Context window"
                        value={`${used()}% used`}
                        detail={`${formatTokenCount(context().usedTokens)} of ${formatTokenCount(context().capacityTokens)} tokens`}
                      />
                      <Show when={usage().totalTokens !== null}>
                        <UsageRow
                          title="Session total"
                          value={`${formatTokenCount(usage().totalTokens ?? 0)} tokens`}
                        />
                      </Show>
                      <For each={usage().rateLimits}>
                        {(limit) => (
                          <UsageRow
                            title={formatRateLimitLabel(limit)}
                            value={formatUsedPercent(limit.usedPercent)}
                            detail={
                              limit.resetsAtMs === null
                                ? undefined
                                : `Resets ${formatResetTime(limit.resetsAtMs)}`
                            }
                          />
                        )}
                      </For>
                    </div>
                  </Portal>
                </Show>
              </button>
            );
          }}
        </Show>
      )}
    </Show>
  );
}

function UsageRow(props: {
  title: string;
  value: string;
  detail?: string | undefined;
}): JSX.Element {
  return (
    <div class="agent-usage-tooltip-section">
      <span class="agent-usage-tooltip-title">{props.title}</span>
      <span class="agent-usage-tooltip-value">{props.value}</span>
      <Show when={props.detail}>
        {(detail) => <span class="agent-usage-tooltip-detail">{detail()}</span>}
      </Show>
    </div>
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
