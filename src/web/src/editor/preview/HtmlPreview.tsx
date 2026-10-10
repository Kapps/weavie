import { createResource, type JSX, Match, onCleanup, onMount, Switch } from "solid-js";
import { grantHtmlPreview, releaseHtmlPreview } from "../../bridge";
import { basename } from "../fs-path";
import { withBase } from "./html-preview-document";
import type { PreviewProps } from "./PreviewPane";

// The page runs its own scripts in an opaque-origin frame (no allow-same-origin), so it can't reach Weavie's
// document, bridge, or cookies; relative assets resolve through a workspace-scoped grant revoked on unmount.
export default function HtmlPreview(props: PreviewProps): JSX.Element {
  let host!: HTMLDivElement;
  const session = props.session();
  const pending = grantHtmlPreview(session, props.path());
  const [grant] = createResource(() => pending);
  onCleanup(() => {
    pending.then(
      (granted) => releaseHtmlPreview(session, granted.grant),
      () => undefined,
    );
  });
  onMount(() => onCleanup(props.bind(host)));

  return (
    <div class="editor-preview editor-preview-html" data-kind="editor" tabindex="0" ref={host}>
      <Switch>
        <Match when={grant.error as Error | undefined}>
          {(error) => (
            <div class="editor-preview-error">
              Could not preview {basename(props.path())}: {error().message}
            </div>
          )}
        </Match>
        <Match when={grant()}>
          {(granted) => (
            <iframe
              class="editor-preview-html-frame"
              title={basename(props.path())}
              sandbox="allow-scripts"
              srcdoc={withBase(props.content(), granted().base)}
            />
          )}
        </Match>
      </Switch>
    </div>
  );
}
