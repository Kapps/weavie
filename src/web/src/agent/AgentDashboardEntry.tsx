import { For, type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { AgentBlockEntry } from "./AgentBlockEntry";
import { AgentCardShell } from "./AgentCardShell";
import type { AgentBlockTranscriptEntry, AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { blockUpdatedAt, ownCardOnFocus, setCardCollapsed } from "./agent-cards";
import { agentBlock } from "./block-store";

/** A dashboard's card: its blocks at full size, or, collapsed, what it holds and when it last changed. */
export function AgentDashboardEntry(props: {
  entry: AgentTranscriptEntry;
  session: ClientSession;
}): JSX.Element {
  const dashboardId = props.entry.dashboardId!;
  const conversationId = props.entry.conversationId ?? null;
  const card = { kind: "dashboard", conversationId, dashboardId } as const;
  const collapsed = () => props.entry.asideCollapsed === true;
  const blocks = () => (props.entry.asideEntries ?? []) as AgentBlockTranscriptEntry[];
  const toggle = () => setCardCollapsed(props.session, card, !collapsed());
  const own = ownCardOnFocus(() => ({ session: props.session, card, toggle }));

  return (
    <AgentCardShell
      label={`Dashboard · ${props.entry.summary ?? dashboardId}`}
      collapsed={collapsed()}
      meta={
        <Show when={collapsed()}>
          <small>{dashboardMeta(props.session, blocks())}</small>
        </Show>
      }
      onToggle={toggle}
      card={card}
      onOwn={own}
      data={{ "data-agent-dashboard": dashboardId }}
    >
      <Show when={!collapsed()}>
        <div class="agent-dashboard-blocks">
          <For each={blocks()}>
            {(block) => <AgentBlockEntry entry={block} session={props.session} height={null} />}
          </For>
        </div>
      </Show>
    </AgentCardShell>
  );
}

/** "4 charts · 1 table · updated 14:32", leaving out what a dashboard doesn't have. */
function dashboardMeta(
  session: ClientSession,
  blocks: readonly AgentBlockTranscriptEntry[],
): string {
  const count = (kind: "chart" | "table") =>
    blocks.filter((block) => block.block.kind === kind).length;
  const latest = Math.max(
    0,
    ...blocks.map((block) => agentBlock(session, block.block.key)?.block.updatedAtMs ?? 0),
  );
  return [
    plural(count("chart"), "chart"),
    plural(count("table"), "table"),
    latest > 0 ? blockUpdatedAt(latest) : "",
  ]
    .filter((part) => part.length > 0)
    .join(" · ");
}

const plural = (count: number, noun: string): string =>
  count === 0 ? "" : `${count} ${noun}${count === 1 ? "" : "s"}`;
