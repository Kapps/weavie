import { createEffect, createSignal, type JSX, onCleanup, onMount, Show } from "solid-js";
import { currentEditorOptions, onEditorOptionsChanged } from "../../editor-options";
import { basename } from "../fs-path";
import { loadMedia, mediaDoc, releaseMedia } from "./media-store";
import { mediaTypeOf } from "./media-types";

/**
 * The media-tab surface: the active image/video file rendered over the (kept-mounted) Monaco host, at the
 * same layer as the Preview/Web overlays. Bytes come from media-store as a Blob URL; a `<video>` gets native
 * controls (autoplaying unless editor.videoAutoplay is off), and the pane focuses itself on mount so the
 * keyboard reaches them (space/arrows). Errors — a
 * too-large file, a failed read, a deletion — render loudly in the pane where the user is looking.
 */
export default function MediaPane(props: { path: () => string }): JSX.Element {
  let host!: HTMLDivElement;

  // Fetch on mount and on every tab switch within the pane, releasing the outgoing path's Blob URL. The
  // loaded path is captured in a plain variable because disposal runs AFTER the driving memo has already
  // recomputed to null — an onCleanup that re-read props.path() would release nothing and leak the blob.
  let loaded: string | undefined;
  createEffect(() => {
    const path = props.path();
    if (loaded !== undefined && loaded !== path) {
      releaseMedia(loaded);
    }
    loaded = path;
    loadMedia(path);
  });
  onCleanup(() => {
    if (loaded !== undefined) {
      releaseMedia(loaded);
    }
  });
  onMount(() => host.focus());

  // Live view of editor.videoAutoplay — toggling it updates the mounted element, so the next load honors it.
  const [autoplay, setAutoplay] = createSignal(currentEditorOptions().videoAutoplay);
  onCleanup(onEditorOptionsChanged((options) => setAutoplay(options.videoAutoplay)));

  const doc = (): ReturnType<typeof mediaDoc> => mediaDoc(props.path());

  return (
    <div class="editor-media" data-kind="editor" tabindex="0" ref={host}>
      <Show when={doc()?.url !== undefined}>
        <Show
          when={mediaTypeOf(props.path())?.kind === "video"}
          fallback={
            <img class="editor-media-content" src={doc()?.url} alt={basename(props.path())} />
          }
        >
          {/* biome-ignore lint/a11y/useMediaCaption: workspace video files carry no caption tracks. */}
          <video class="editor-media-content" src={doc()?.url} controls autoplay={autoplay()} />
        </Show>
      </Show>
      <Show when={doc()?.status === "error"}>
        <div class="editor-media-notice">{doc()?.error}</div>
      </Show>
      <Show when={doc()?.status === "loading" && doc()?.url === undefined}>
        <div class="editor-media-notice">Loading {basename(props.path())}…</div>
      </Show>
    </div>
  );
}
