import { Star } from "lucide-solid";
import { Show } from "solid-js";
import type { ExtensionChoice } from "./picker-state";

export function ThemeExtensionDetails(props: { extension: ExtensionChoice }) {
  return (
    <small class="theme-extension-details">
      <span class="theme-publisher">Publisher: {props.extension.namespace}</span>
      <span class="theme-downloads">
        <span aria-hidden="true">↓ </span>
        {props.extension.downloadCount.toLocaleString()} downloads
      </span>
      <span class="theme-rating">
        <Show when={props.extension.averageRating != null} fallback="Rating unavailable">
          <Star class="theme-rating-star" size={12} aria-hidden="true" />
          {props.extension.averageRating?.toFixed(1)} / 5
        </Show>
      </span>
      <span>
        {props.extension.reviewCount == null
          ? "Review count unavailable"
          : `${props.extension.reviewCount.toLocaleString()} ${props.extension.reviewCount === 1 ? "review" : "reviews"}`}
      </span>
    </small>
  );
}
