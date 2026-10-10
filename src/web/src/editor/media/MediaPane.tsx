import {
  createEffect,
  createMemo,
  createSignal,
  type JSX,
  onCleanup,
  onMount,
  Show,
} from "solid-js";
import { type ClientSession, mediaResourceUrl } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { CommandIds } from "../../commands/types";
import { currentEditorOptions, onEditorOptionsChanged } from "../../editor-options";
import { basename, samePath } from "../fs-path";
import { createPanZoom } from "../pan-zoom";
import { registerMediaPane } from "./media-rotate";
import { mediaTypeOf } from "./media-types";

/**
 * The media-tab surface: the active image/video file rendered over the (kept-mounted) Monaco host, at the
 * same layer as the Preview/Web overlays. The browser streams bytes directly from the workspace HTTP server.
 * Videos get native controls. The view zooms/pans (wheel, pinch, drag, +/-/0) and rotates a quarter turn,
 * refitting so a sideways video fills a portrait screen; a video's double-click stays the browser's
 * fullscreen toggle. Failed reads and deletions render loudly in the pane.
 */
export default function MediaPane(props: {
  session: ClientSession;
  path: string;
  bind(element: HTMLElement): () => void;
}): JSX.Element {
  let host!: HTMLDivElement;
  let content: HTMLElement | undefined;
  const zoom = createPanZoom(() => host);
  const [rotated, setRotated] = createSignal(false);
  // A quarter turn swaps the fitted box's axes, so it rescales to fit the pane the other way round.
  const [fitScale, setFitScale] = createSignal(1);
  // An image stays capped at its natural size, as unrotated; a video fills, like fullscreen playback.
  const refit = (): void => {
    const w = content?.offsetWidth ?? 0;
    const h = content?.offsetHeight ?? 0;
    const cap =
      content instanceof HTMLImageElement ? Math.max(1, content.naturalWidth / w) : Infinity;
    setFitScale(
      rotated() && w > 0 && h > 0 ? Math.min(host.clientWidth / h, host.clientHeight / w, cap) : 1,
    );
  };
  const rotate = (): void => {
    zoom.reset();
    setRotated((value) => !value);
    refit();
  };
  onMount(() => {
    onCleanup(props.bind(host));
    onCleanup(registerMediaPane(props.session, rotate));
    host.addEventListener("wheel", zoom.onWheel, { passive: false });
    const observer = new ResizeObserver(refit);
    observer.observe(host);
    onCleanup(() => observer.disconnect());
  });
  const loaded = (): void => {
    setStatus("ready");
    refit();
  };

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
    <div
      class="editor-media"
      role="application"
      aria-label={basename(props.path)}
      classList={{ zoomed: zoom.scale() > 1, panning: zoom.panning() }}
      data-kind="editor"
      tabindex="0"
      ref={host}
      {...zoom.handlers}
      onKeyDown={(event) => {
        if (!event.ctrlKey && !event.metaKey && !event.altKey && zoom.zoomKey(event.key)) {
          event.preventDefault();
        }
      }}
    >
      <div
        class="editor-media-zoom"
        style={`transform: ${zoom.transform()}${rotated() ? ` rotate(90deg) scale(${fitScale()})` : ""}`}
      >
        <Show when={url()} keyed>
          {(src) => (
            <Show
              when={mediaTypeOf(props.path)?.kind === "video"}
              fallback={
                <img
                  class="editor-media-content"
                  ref={(element) => {
                    content = element;
                  }}
                  src={src}
                  alt={basename(props.path)}
                  onLoad={loaded}
                  onError={failed}
                />
              }
            >
              {/* biome-ignore lint/a11y/useMediaCaption: workspace video files carry no caption tracks. */}
              <video
                class="editor-media-content"
                ref={(element) => {
                  content = element;
                }}
                src={src}
                controls
                preload="metadata"
                autoplay={autoplay()}
                playsinline
                onLoadedMetadata={loaded}
                onDblClick={(event) => event.stopPropagation()}
                onError={failed}
              />
            </Show>
          )}
        </Show>
      </div>
      <Show when={status() === "ready"}>
        <button
          type="button"
          class="editor-media-rotate"
          title={`Rotate 90°${keyHint(CommandIds.rotateMedia)}`}
          aria-label="Rotate 90°"
          onPointerDown={(event) => event.stopPropagation()}
          onDblClick={(event) => event.stopPropagation()}
          onClick={rotate}
        >
          <svg
            viewBox="0 0 24 24"
            width="16"
            height="16"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <path d="M21 12a9 9 0 1 1-3-6.7L21 8" />
            <path d="M21 3v5h-5" />
          </svg>
        </button>
      </Show>
      <Show when={zoom.hint()}>
        <div class="editor-media-hint">{zoom.hint()}</div>
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
