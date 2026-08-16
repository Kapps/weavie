import { createSignal, createUniqueId, For, type JSX, Show } from "solid-js";
import { Portal } from "solid-js/web";
import type { AgentUsageLimit, ClientSession } from "../bridge";
import {
  contextUsedPercent,
  formatLimitLabel,
  formatLimitValue,
  formatResetTime,
  formatTokenCount,
} from "./AgentUsageFormat";
import { agentUsage } from "./agent-usage-store";

export function AgentUsageIndicator(props: { session: ClientSession | null }): JSX.Element {
  const usage = () => agentUsage(props.session);
  const tooltipId = `agent-usage-${createUniqueId()}`;
  const [anchor, setAnchor] = createSignal<DOMRect | null>(null);
  // Windows are stamped when the tooltip opens, so one whose reset has already passed is never shown.
  const [openedAtMs, setOpenedAtMs] = createSignal(0);
  let tooltip: HTMLDivElement | undefined;
  const open = (element: HTMLElement): void => {
    const bounds = element.getBoundingClientRect();
    setOpenedAtMs(Date.now());
    setAnchor(bounds);
    queueMicrotask(() => tooltip !== undefined && placeTooltip(tooltip, bounds));
  };
  const liveLimits = (): AgentUsageLimit[] =>
    (usage()?.limits ?? []).filter(
      (limit) => limit.resetsAtMs === null || limit.resetsAtMs > openedAtMs(),
    );

  return (
    <Show when={usage()?.contextWindow}>
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
                  <For each={liveLimits()}>
                    {(limit) => (
                      <UsageRow
                        title={formatLimitLabel(limit.id)}
                        value={formatLimitValue(limit)}
                        status={limit.status}
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
  );
}

function UsageRow(props: {
  title: string;
  value: string;
  detail?: string | undefined;
  status?: AgentUsageLimit["status"] | undefined;
}): JSX.Element {
  return (
    <div class="agent-usage-tooltip-section">
      <span class="agent-usage-tooltip-title">{props.title}</span>
      <span class="agent-usage-tooltip-value" data-status={props.status}>
        {props.value}
      </span>
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
