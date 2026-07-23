import { createMemo, type JSX, onMount, Show } from "solid-js";
import { AgentMarkdown } from "../../agent/AgentMarkdown";
import { agentPlan } from "./plan-store";

// A read-only virtual editor document. AgentMarkdown's renderer disables HTML, images, Mermaid, and unsafe links.
export default function PlanView(props: { path: () => string }): JSX.Element {
  let host!: HTMLDivElement;
  const document = createMemo(() => agentPlan(props.path()));

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
            <AgentMarkdown content={plan().markdown} />
          </article>
        )}
      </Show>
    </div>
  );
}
