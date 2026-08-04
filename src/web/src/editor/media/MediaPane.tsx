import { createEffect, createMemo, createSignal, type JSX, onCleanup, Show } from "solid-js";
import { type ClientSession, mediaResourceUrl } from "../../bridge";
import { currentEditorOptions, onEditorOptionsChanged } from "../../editor-options";
import { preserveEditorFocusOnMount } from "../focus-on-mount";
import { basename, samePath } from "../fs-path";
import { mediaTypeOf } from "./media-types";

/**
 * The media-tab surface: the active image/video file rendered over the (kept-mounted) Monaco host, at the
 * same layer as the Preview/Web overlays. The browser streams bytes directly from the workspace HTTP server.
 * Videos get native controls. Failed reads and deletions render loudly in the pane.
 */
export default function MediaPane(props: {
  session: ClientSession;
  path: string;
  focusOnMount: boolean;
}): JSX.Element {
  let host!: HTMLDivElement;
  preserveEditorFocusOnMount(
    () => host,
    () => props.focusOnMount,
  );

  // Live view of editor.videoAutoplay — toggling it updates the mounted element, so the next load honors it.
  const [autoplay, setAutoplay] = createSignal(currentEditorOptions().videoAutoplay);
  onCleanup(onEditorOptionsChanged((options) => setAutoplay(options.videoAutoplay)));

  const [revision, setRevision] = createSignal(0);
  const [status, setStatus] = createSignal<"loading" | "ready" | "error">("loading");
  const [error, setError] = createSignal<string | null>(null);
  const url = createMemo(() => mediaResourceUrl(props.session, props.path, revision()));
  createEffect(() => {
    if (url() === null) {
      setStatus("error");
      setError(`No media endpoint is available for ${basename(props.path)}.`);
    } else {
      setStatus("loading");
      setError(null);
    }
  });
  createEffect(() => {
    const session = props.session;
    const off = session.feature("files").on<{
      changes: { path: string; kind: "updated" | "added" | "deleted" }[];
    }>("changed", ({ changes }) => {
      const change = changes.find((candidate) => samePath(candidate.path, props.path));
      if (change?.kind === "deleted") {
        setStatus("error");
        setError(`${basename(props.path)} was deleted.`);
      } else if (change !== undefined) {
        setRevision((value) => value + 1);
      }
    });
    onCleanup(off);
  });

  const failed = (): void => {
    setStatus("error");
    setError(`Unable to load ${basename(props.path)}.`);
  };

  return (
    <div class="editor-media" data-kind="editor" tabindex="0" ref={host}>
      <Show when={url()} keyed>
        {(src) => (
          <Show
            when={mediaTypeOf(props.path)?.kind === "video"}
            fallback={
              <img
                class="editor-media-content"
                src={src}
                alt={basename(props.path)}
                onLoad={() => setStatus("ready")}
                onError={failed}
              />
            }
          >
            {/* biome-ignore lint/a11y/useMediaCaption: workspace video files carry no caption tracks. */}
            <video
              class="editor-media-content"
              src={src}
              controls
              preload="metadata"
              autoplay={autoplay()}
              playsinline
              onLoadedMetadata={() => setStatus("ready")}
              onError={failed}
            />
          </Show>
        )}
      </Show>
      <Show when={status() === "error"}>
        <div class="editor-media-notice">{error()}</div>
      </Show>
      <Show when={status() === "loading"}>
        <div class="editor-media-notice">Loading {basename(props.path)}…</div>
      </Show>
    </div>
  );
}
