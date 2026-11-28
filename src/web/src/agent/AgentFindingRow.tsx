import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { Disclosure } from "./AgentDisclosure";
import { AgentMarkdown } from "./AgentMarkdown";
import { blockUpdatedAt } from "./agent-cards";
import { agentBlock, findingOpen } from "./block-store";
import "./agent-findings.css";

const ROW = "agent-disclosure-row ui-row ui-row-stack agent-finding-row";

/**
 * One pinned finding where it is shown, on the board or in a reply: its title, opening to its detail. `title` stands
 * in until the block's state arrives.
 */
export function AgentFindingRow(props: {
  session: ClientSession;
  blockKey: string;
  /** Where this row is, so the board's copy and a reply's open independently. */
  placement: string;
  title: string;
}): JSX.Element {
  const block = () => agentBlock(props.session, props.blockKey)?.block;
  const [open, setOpen] = findingOpen(props.session, props.placement);
  const cacheKey = {};
  let root: HTMLDivElement | undefined;
  const label = (): JSX.Element => (
    <>
      <span class="agent-finding-title">{block()?.title ?? props.title}</span>
      <Show when={block()?.updatedAtMs}>
        {(time) => <span class="agent-entry-status">{blockUpdatedAt(time())}</span>}
      </Show>
    </>
  );
  return (
    <div class="agent-finding" ref={root}>
      <Show
        when={block()?.finding?.detail}
        fallback={
          <div class={ROW}>
            <span class="ui-icon" />
            {label()}
          </div>
        }
      >
        {(detail) => (
          <Disclosure
            class="agent-row-disclosure"
            summaryClass={ROW}
            label={label()}
            open={open()}
            onToggle={(next) => {
              setOpen(next);
              // The board rests at its end, so a row opened above that would grow out of view.
              if (!next) return;
              requestAnimationFrame(() =>
                root?.querySelector("summary")?.scrollIntoView({ block: "nearest" }),
              );
            }}
          >
            <div class="agent-disclosure-body">
              <AgentMarkdown
                reply={null}
                cacheKey={cacheKey}
                content={detail()}
                flavor="agent"
                renderMermaid={true}
                session={props.session}
              />
            </div>
          </Disclosure>
        )}
      </Show>
    </div>
  );
}
