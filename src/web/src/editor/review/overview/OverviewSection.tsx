import { ChevronRight } from "lucide-solid";
import { type JSX, Show } from "solid-js";

/** One collapsible overview section: a heading that toggles it, then its content. */
export function OverviewSection(props: {
  id: string;
  title: string;
  /** Quiet context after the title, e.g. a count. */
  meta: JSX.Element;
  collapsed: boolean;
  onToggle: (collapsed: boolean) => void;
  children: JSX.Element;
}): JSX.Element {
  return (
    <section class="session-overview-section" data-section={props.id}>
      <button
        type="button"
        class="session-overview-heading"
        aria-expanded={!props.collapsed}
        onClick={() => props.onToggle(!props.collapsed)}
      >
        <ChevronRight size="1em" class="session-overview-chevron" />
        <span class="session-overview-title">{props.title}</span>
        <span class="session-overview-meta">{props.meta}</span>
      </button>
      <Show when={!props.collapsed}>
        <div class="session-overview-body">{props.children}</div>
      </Show>
    </section>
  );
}
