import { Pause, Play, Volume2, VolumeX } from "lucide-solid";
import { createSignal, type JSX, onCleanup } from "solid-js";
import { notify } from "../../notify/notify";

const clock = (seconds: number): string => {
  const whole = Number.isFinite(seconds) ? Math.floor(seconds) : 0;
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, "0")}`;
};

/** Plays or pauses `video`; a refused play surfaces as a toast (an interrupting pause is not a failure). */
export function togglePlayback(video: HTMLVideoElement): void {
  if (!video.paused) {
    video.pause();
    return;
  }
  video.play().catch((error: unknown) => {
    if (!(error instanceof DOMException && error.name === "AbortError")) {
      notify("warn", `Unable to play the video: ${String(error)}`);
    }
  });
}

/**
 * The media pane's own playback bar. It replaces the native controls, whose fullscreen fullscreens the bare
 * <video> (dropping the pane's rotation) and which swallow touches on iOS, so pinch/pan never reach the pane.
 */
export default function VideoControls(props: { video: HTMLVideoElement }): JSX.Element {
  const video = props.video;
  const [paused, setPaused] = createSignal(video.paused);
  const [muted, setMuted] = createSignal(video.muted);
  const [time, setTime] = createSignal(video.currentTime);
  const [duration, setDuration] = createSignal(video.duration);
  const sync = (): void => {
    setPaused(video.paused);
    setMuted(video.muted);
    setTime(video.currentTime);
    setDuration(video.duration);
  };
  const events = ["play", "pause", "timeupdate", "durationchange", "volumechange", "seeked"];
  for (const name of events) video.addEventListener(name, sync);
  onCleanup(() => {
    for (const name of events) video.removeEventListener(name, sync);
  });

  return (
    <div class="editor-media-controls">
      <button
        type="button"
        aria-label={paused() ? "Play" : "Pause"}
        onClick={() => togglePlayback(video)}
      >
        {paused() ? <Play size={16} /> : <Pause size={16} />}
      </button>
      <span class="editor-media-time">{clock(time())}</span>
      <input
        type="range"
        aria-label="Seek"
        min="0"
        max={Number.isFinite(duration()) ? duration() : 0}
        step="any"
        value={time()}
        onInput={(event) => {
          video.currentTime = event.currentTarget.valueAsNumber;
        }}
      />
      <span class="editor-media-time">{clock(duration())}</span>
      <button
        type="button"
        aria-label={muted() ? "Unmute" : "Mute"}
        onClick={() => {
          video.muted = !video.muted;
        }}
      >
        {muted() ? <VolumeX size={16} /> : <Volume2 size={16} />}
      </button>
    </div>
  );
}
