import { type JSX, Show } from "solid-js";
import { relativeTime } from "./blame-model";

const FULL = new Intl.DateTimeFormat(undefined, { dateStyle: "full", timeStyle: "long" });

/** "3 days ago" for a Unix-seconds time; hovering shows the full local date and time with its timezone. */
export function RelativeTime(props: { seconds: number }): JSX.Element {
  return (
    <Show when={Number.isFinite(props.seconds)}>
      <time
        dateTime={new Date(props.seconds * 1000).toISOString()}
        title={FULL.format(props.seconds * 1000)}
      >
        {relativeTime(props.seconds, Date.now() / 1000)}
      </time>
    </Show>
  );
}
