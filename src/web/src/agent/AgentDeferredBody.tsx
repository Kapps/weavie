import { createResource, createSignal, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { loadAgentBody } from "./pane-store";

export function AgentDeferredBody(props: {
  children: JSX.Element;
  message: AgentPaneUpdate | null;
  session: ClientSession;
}): JSX.Element {
  let container: HTMLDivElement | undefined;
  const [visible, setVisible] = createSignal(false);
  onMount(() => {
    const observer = new IntersectionObserver(
      ([entry]) => setVisible(entry?.isIntersecting === true),
      {
        root: container!.closest(".agent-body"),
      },
    );
    observer.observe(container!);
    onCleanup(() => observer.disconnect());
  });
  const [body, { refetch }] = createResource(
    () => (visible() && props.message?.bodyDeferred === true ? props.message : null),
    (message) => loadAgentBody(props.session, message),
  );
  onCleanup(
    props.session.connection.onHello(() => {
      if (visible() && props.message?.bodyDeferred === true) void refetch();
    }),
  );
  return (
    <div ref={container}>
      <Show
        when={props.message?.bodyDeferred !== true}
        fallback={
          <span class="agent-entry-status" role="status">
            {body.error ? `Couldn't load this message: ${String(body.error)}` : "Loading message…"}
          </span>
        }
      >
        {props.children}
      </Show>
    </div>
  );
}
