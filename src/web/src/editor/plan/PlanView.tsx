import { createMemo, type JSX, onMount, Show } from "solid-js";
import { AgentMarkdown } from "../../agent/AgentMarkdown";
import type { ClientSession } from "../../bridge";
import { agentPlan } from "./plan-store";

// A read-only virtual editor document. AgentMarkdown disables HTML, images, and unsafe links; completed Mermaid
// fences hydrate through the shared preview renderer.
export default function PlanView(props: { session: ClientSession; path: string }): JSX.Element {
  let host!: HTMLDivElement;
  const document = createMemo(() => agentPlan(props.session, props.path));

  onMount(() => host.focus());

  return (
    <div class="editor-plan" data-kind="editor" tabindex="0" ref={host}>
      <Show
        when={document()}
        fallback={<div class="editor-plan-notice">This plan is no longer available.</div>}
      >
        {(plan) => (
          <article class="editor-plan-body">
            <header class="editor-plan-head">
              <span class="editor-plan-kicker">Plan</span>
              <h1>{plan().title}</h1>
            </header>
            <AgentMarkdown content={plan().markdown} renderMermaid={true} session={props.session} />
          </article>
        )}
      </Show>
    </div>
  );
}
