import { createEffect, createMemo, createSignal, For, type JSX } from "solid-js";
import type { ClientSession } from "../bridge";
import { activePathFor, openTabsFor } from "./session-store";

/**
 * Retains each activated web tab's browsing context for the lifetime of its exact session and tab. Restored
 * tabs stay dormant until first activation, so loading a workspace never contacts their URLs in the background.
 */
export default function WebTabStack(props: {
  sessions: () => ClientSession[];
  selectedSession: () => ClientSession | null;
}): JSX.Element {
  return (
    <For each={props.sessions()}>
      {(session) => <SessionWebTabs session={session} selectedSession={props.selectedSession} />}
    </For>
  );
}

function SessionWebTabs(props: {
  session: ClientSession;
  selectedSession: () => ClientSession | null;
}): JSX.Element {
  const [retainedUrls, setRetainedUrls] = createSignal<string[]>([]);
  const activeUrl = createMemo<string | null>(() => {
    if (props.selectedSession() !== props.session) {
      return null;
    }
    const active = activePathFor(props.session);
    return openTabsFor(props.session).some((tab) => tab.kind === "web" && tab.path === active)
      ? active
      : null;
  });

  createEffect(() => {
    const openUrls = new Set(
      openTabsFor(props.session).flatMap((tab) => (tab.kind === "web" ? [tab.path] : [])),
    );
    const selectedUrl = activeUrl();
    setRetainedUrls((previous) => {
      const next = previous.filter((url) => openUrls.has(url));
      if (selectedUrl !== null && !next.includes(selectedUrl)) {
        next.push(selectedUrl);
      }
      return next.length === previous.length && next.every((url, index) => url === previous[index])
        ? previous
        : next;
    });
  });

  return (
    <For each={retainedUrls()}>
      {(url) => {
        const active = (): boolean => activeUrl() === url;
        return (
          <div
            class="editor-web"
            data-kind="editor"
            data-url={url}
            hidden={!active()}
            inert={!active()}
            tabindex={active() ? 0 : undefined}
          >
            <iframe class="editor-web-frame" src={url} title="Web preview" />
          </div>
        );
      }}
    </For>
  );
}
