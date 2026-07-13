import {
  createEffect,
  createMemo,
  createSignal,
  type JSX,
  onCleanup,
  onMount,
  Show,
} from "solid-js";
import { mediaResourceUrl, onHostMessage } from "../../bridge";
import { currentEditorOptions, onEditorOptionsChanged } from "../../editor-options";
import { basename, samePath } from "../fs-path";
import { mediaTypeOf } from "./media-types";

/**
 * The media-tab surface: the active image/video file rendered over the (kept-mounted) Monaco host, at the
 * same layer as the Preview/Web overlays. The browser streams bytes directly from the workspace HTTP server.
 * Videos get native controls, and the pane focuses itself so the keyboard reaches them. Failed reads and
 * deletions render loudly in the pane.
 */
export default function MediaPane(props: {
  backendId: () => string;
  sessionId: () => string;
  path: () => string;
}): JSX.Element {
  let host!: HTMLDivElement;
  onMount(() => host.focus());

  // Live view of editor.videoAutoplay — toggling it updates the mounted element, so the next load honors it.
  const [autoplay, setAutoplay] = createSignal(currentEditorOptions().videoAutoplay);
  onCleanup(onEditorOptionsChanged((options) => setAutoplay(options.videoAutoplay)));

  const [revision, setRevision] = createSignal(0);
  const [status, setStatus] = createSignal<"loading" | "ready" | "error">("loading");
  const [error, setError] = createSignal<string | null>(null);
  const url = createMemo(() =>
    mediaResourceUrl(props.backendId(), props.sessionId(), props.path(), revision()),
  );
  createEffect(() => {
    if (url() === null) {
      setStatus("error");
      setError(`No media endpoint is available for ${basename(props.path())}.`);
    } else {
      setStatus("loading");
      setError(null);
    }
  });
  onCleanup(
    onHostMessage((message) => {
      if (message.type !== "fs-change") {
        return;
      }
      const change = message.changes.find((candidate) => samePath(candidate.path, props.path()));
      if (change?.kind === "deleted") {
        setStatus("error");
        setError(`${basename(props.path())} was deleted.`);
      } else if (change !== undefined) {
        setRevision((value) => value + 1);
      }
    }),
  );

  const failed = (): void => {
    setStatus("error");
    setError(`Unable to load ${basename(props.path())}.`);
  };

  return (
    <div class="editor-media" data-kind="editor" tabindex="0" ref={host}>
      <Show when={url()} keyed>
        {(src) => (
          <Show
            when={mediaTypeOf(props.path())?.kind === "video"}
            fallback={
              <img
                class="editor-media-content"
                src={src}
                alt={basename(props.path())}
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
        <div class="editor-media-notice">Loading {basename(props.path())}…</div>
      </Show>
    </div>
  );
}
