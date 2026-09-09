import {
  createEffect,
  createMemo,
  createResource,
  createSignal,
  ErrorBoundary,
  For,
  type JSX,
  lazy,
  Match,
  onCleanup,
  Show,
  Suspense,
  Switch,
} from "solid-js";
import { type ClientSession, selectedSession } from "../bridge";
import { setContext } from "../commands/context";
import type { EditorController } from "./editor-controller";
import MediaPane from "./media/MediaPane";
import { mediaTypeOf } from "./media/media-types";
import { canPreview } from "./preview/preview-registry";
import { activeTabFor, openTabsFor, tabOwnerFor } from "./session-store";
import { sourceDoc } from "./source/source-store";
import { tabKind } from "./tab-entry";
import { focusTabContent, type TabOwner } from "./tab-owner";
import { isPreviewMode } from "./view-mode-store";

const PlanView = lazy(() => import("./plan/PlanView"));
const PreviewPane = lazy(() => import("./preview/PreviewPane"));
const SourceView = lazy(() => import("./source/SourceView"));
const ReviewTab = lazy(() => import("./review/ReviewTab"));

/** Each tab mounts through the same lifecycle, including retained browsing contexts. */
function ActiveTabContent(props: {
  tab: TabOwner;
  controller: EditorController;
  active: () => boolean;
}): JSX.Element {
  const { tab, controller } = props;
  const kind = tabKind(tab.entry);
  const file = kind === "file";
  const media = file && mediaTypeOf(tab.entry.path) !== null;
  const [view, setView] = createSignal<HTMLElement>();
  const bind = (element: HTMLElement): (() => void) => {
    setView(element);
    return () => {
      if (view() === element) setView(undefined);
    };
  };
  const preview = () =>
    file &&
    !media &&
    canPreview(tab.entry.path) &&
    isPreviewMode(tab.session, tab.entry.path) &&
    !controller.reviewActive();
  const overlay = () => !file || media || preview();
  const [host] = createResource(() => controller.hostReady);
  createEffect(() => {
    const ready = host();
    if (
      !props.active() ||
      ready === undefined ||
      kind === "review" ||
      (overlay() && view() === undefined)
    )
      return;
    const presenter =
      file && !media
        ? controller.filePresenter(tab, () => (overlay() ? view() : undefined))
        : {
            text: false,
            capture: () => ({ state: null, text: null }),
            restore: async () => ready.clear(),
            focus: () => {
              focusTabContent(view()!);
            },
            actions: () => undefined,
          };
    const unregister = tab.mount(presenter);
    setContext("diffActive", false);
    onCleanup(() => {
      controller.captureTab(tab);
      unregister();
    });
  });
  return (
    <div
      class="editor-tab-content"
      data-kind="editor"
      hidden={!props.active()}
      inert={!props.active()}
    >
      <Suspense>
        <Switch>
          <Match when={kind === "review"}>
            <Show when={host()}>
              {(ready) => <ReviewTab tab={tab} controller={controller} host={ready()} />}
            </Show>
          </Match>
          <Match when={kind === "web"}>
            <div
              class="editor-web"
              data-kind="editor"
              data-url={tab.entry.path}
              tabindex={0}
              ref={(element) => {
                onCleanup(bind(element));
              }}
            >
              <iframe
                class="editor-web-frame"
                src={tab.entry.path}
                title="Web preview"
                sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-popups-to-escape-sandbox allow-downloads allow-modals allow-presentation"
              />
            </div>
          </Match>
          <Match when={kind === "source"}>
            <SourceView
              session={tab.session}
              target={() => tab.entry.path}
              doc={() => sourceDoc(tab.session, tab.entry.path)}
              bind={bind}
            />
          </Match>
          <Match when={kind === "plan"}>
            <PlanView session={tab.session} path={tab.entry.path} bind={bind} />
          </Match>
          <Match when={media}>
            <MediaPane session={tab.session} path={tab.entry.path} bind={bind} />
          </Match>
          <Match when={preview()}>
            <PreviewPane
              session={() => tab.session}
              path={() => tab.entry.path}
              content={controller.activeContent}
              bind={bind}
            />
          </Match>
        </Switch>
      </Suspense>
    </div>
  );
}

/** Restored tabs stay dormant until selected; only browsing contexts need retention while hidden. */
export default function TabContent(props: {
  controller: EditorController;
  sessions: () => ClientSession[];
}): JSX.Element {
  const active = createMemo(() => {
    const session = selectedSession();
    return session === null ? undefined : activeTabFor(session);
  });
  createEffect(() => {
    if (active() === undefined) setContext("diffActive", false);
  });
  const mounted = createMemo<TabOwner[]>((previous) => {
    const live = new Set(
      props
        .sessions()
        .flatMap((session) =>
          openTabsFor(session).map((entry) => tabOwnerFor(session, entry.path)!),
        ),
    );
    const current = active();
    const next = previous.filter(
      (tab) => live.has(tab) && (tab === current || tabKind(tab.entry) === "web"),
    );
    if (current !== undefined && !next.includes(current)) next.push(current);
    return next;
  }, []);
  return (
    <For each={mounted()}>
      {(tab) => (
        <ErrorBoundary
          fallback={(error: unknown) => {
            const failure = error instanceof Error ? error : new Error(String(error));
            tab.failed(failure);
            return (
              <div class="editor-tab-error" role="alert" hidden={active() !== tab}>
                Could not open this tab: {failure.message}
              </div>
            );
          }}
        >
          <ActiveTabContent
            tab={tab}
            controller={props.controller}
            active={() => active() === tab}
          />
        </ErrorBoundary>
      )}
    </For>
  );
}
