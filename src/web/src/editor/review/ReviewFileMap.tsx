import { For, type JSX } from "solid-js";
import { type ReviewOverview, reviewProgress } from "./review-store";

/** A bar docked under the review header: one segment per file, filled by the share of it already kept. */
export function ReviewFileMap(props: {
  overview: () => ReviewOverview;
  current: () => number | undefined;
  displayPath: (path: string) => string;
  onReveal: (index: number) => void;
}): JSX.Element {
  return (
    <nav class="unified-review-map" aria-label="Review files">
      <For each={props.overview().files}>
        {(file, index) => {
          const progress = () => reviewProgress(file);
          const percent = () => Math.round(progress().fraction * 100);
          return (
            <button
              type="button"
              class="unified-review-map-file"
              classList={{ current: props.current() === index() }}
              style={`flex-grow:${progress().total};--reviewed:${percent()}%`}
              title={`${props.displayPath(file.summary().path)} +${file.summary().added} −${file.summary().removed} · ${percent()}% reviewed`}
              aria-current={props.current() === index() ? "true" : undefined}
              onClick={() => props.onReveal(index())}
            />
          );
        }}
      </For>
    </nav>
  );
}
