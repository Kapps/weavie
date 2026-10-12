import { Maximize, Minimize, RotateCw } from "lucide-solid";
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
import { useCompactMode } from "../../mobile/useCompactMode";
import { basename, samePath } from "../fs-path";
import { createPanZoom } from "../pan-zoom";
import { registerMediaPane } from "./media-commands";
import { mediaTypeOf } from "./media-types";
import VideoControls, { togglePlayback } from "./VideoControls";

/**
 * The media-tab surface: the active image/video file rendered over the (kept-mounted) Monaco host, at the
 * same layer as the Preview/Web overlays. The browser streams bytes directly from the workspace HTTP server.
 * The view zooms/pans (wheel, pinch, drag, +/-/0), rotates a quarter turn by laying the media out in the
 * pane's swapped axes, and goes fullscreen with its rotation intact; on a phone, rotating fills the screen.
 * Videos play through the pane's own controls. Failed reads and deletions render loudly in the pane.
 */
export default function MediaPane(props: {
  session: ClientSession;
  path: string;
  bind(element: HTMLElement): () => void;
}): JSX.Element {
  let host!: HTMLDivElement;
  const zoom = createPanZoom(() => host);
  const compact = useCompactMode();
  const coarse = window.matchMedia("(pointer: coarse)");
  const [rotated, setRotated] = createSignal(false);
  const [fullscreen, setFullscreen] = createSignal(false);
  const [video, setVideo] = createSignal<HTMLVideoElement>();
  // The pane covers the viewport itself (iPhone has no element fullscreen); the Fullscreen API, where the
  // browser grants it, also hides the browser's own chrome.
  const showFullscreen = (on: boolean): void => {
    setFullscreen(on);
    if (on && document.fullscreenEnabled) {
      host.requestFullscreen().catch(() => undefined);
    } else if (!on && document.fullscreenElement === host) {
      void document.exitFullscreen();
    }
  };
  const toggleFullscreen = (): void => showFullscreen(!fullscreen());
  const rotate = (): void => {
    zoom.reset();
    setRotated((value) => !value);
    // A phone held sideways: the turned media takes the whole screen. A narrow desktop window just turns.
    if (compact() && coarse.matches) showFullscreen(rotated());
  };
  // Keeps the browser's fullscreen in step: leaving it (Escape, back) exits, and one granted after a quick
  // toggle back out is released.
  const syncFullscreen = (): void => {
    const granted = document.fullscreenElement === host;
    if (!granted && fullscreen()) setFullscreen(false);
    else if (granted && !fullscreen()) void document.exitFullscreen();
  };
  onMount(() => {
    onCleanup(props.bind(host));
    onCleanup(registerMediaPane(props.session, { rotate, toggleFullscreen }));
    host.addEventListener("wheel", zoom.onWheel, { passive: false });
    document.addEventListener("fullscreenchange", syncFullscreen);
    onCleanup(() => {
      document.removeEventListener("fullscreenchange", syncFullscreen);
      if (document.fullscreenElement === host) void document.exitFullscreen();
    });
  });
  const loaded = (): void => {
    setStatus("ready");
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

  // Escape leaves fullscreen from anywhere in the pane; Space and the shared +/-/0 zoom keys act only on the
  // pane itself, so a focused control keeps its own keys.
  const viewKey = (key: string, onPane: boolean): boolean => {
    const playing = video();
    if (key === "Escape" && fullscreen()) {
      showFullscreen(false);
    } else if (!onPane) {
      return false;
    } else if (key === " " && playing !== undefined) {
      togglePlayback(playing);
    } else {
      return zoom.zoomKey(key);
    }
    return true;
  };
  const tool = (label: string, title: string, click: () => void, icon: JSX.Element) => (
    <button type="button" title={title} aria-label={label} onClick={click}>
      {icon}
    </button>
  );

  return (
    <div
      class="editor-media"
      role="application"
      aria-label={basename(props.path)}
      classList={{
        zoomed: zoom.scale() > 1,
        panning: zoom.panning(),
        rotated: rotated(),
        fullscreen: fullscreen(),
      }}
      data-kind="editor"
      tabindex="0"
      ref={host}
      {...zoom.handlers}
      onKeyDown={(event) => {
        if (
          !event.ctrlKey &&
          !event.metaKey &&
          !event.altKey &&
          viewKey(event.key, event.target === event.currentTarget)
        ) {
          event.preventDefault();
        }
      }}
    >
      <div
        class="editor-media-zoom"
        style={`transform: ${zoom.transform()}${rotated() ? " rotate(90deg)" : ""}`}
      >
        <Show when={url()} keyed>
          {(src) => (
            <Show
              when={mediaTypeOf(props.path)?.kind === "video"}
              fallback={
                <img
                  class="editor-media-content"
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
                ref={setVideo}
                src={src}
                preload="metadata"
                autoplay={autoplay()}
                playsinline
                onLoadedMetadata={loaded}
                onError={failed}
              />
            </Show>
          )}
        </Show>
      </div>
      <Show when={status() === "ready"}>
        {/* Presses on the controls never reach the pane's pan/zoom gestures. */}
        <div
          class="editor-media-chrome"
          role="toolbar"
          aria-label="Media controls"
          onPointerDown={(event) => event.stopPropagation()}
          onDblClick={(event) => event.stopPropagation()}
        >
          <div class="editor-media-tools">
            {tool(
              fullscreen() ? "Exit fullscreen" : "Fullscreen",
              `${fullscreen() ? "Exit fullscreen" : "Fullscreen"}${keyHint(CommandIds.toggleMediaFullscreen)}`,
              toggleFullscreen,
              fullscreen() ? <Minimize size={16} /> : <Maximize size={16} />,
            )}
            {tool(
              "Rotate 90°",
              `Rotate 90°${keyHint(CommandIds.rotateMedia)}`,
              rotate,
              <RotateCw size={16} />,
            )}
          </div>
          <Show when={zoom.hint()}>
            <div class="editor-media-hint">{zoom.hint()}</div>
          </Show>
          <Show when={video()} keyed>
            {(element) => <VideoControls video={element} />}
          </Show>
        </div>
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
