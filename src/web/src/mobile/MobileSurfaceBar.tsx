import { For, type JSX } from "solid-js";

export type MobileSurface = "inbox" | "terminal:claude" | "terminal:shell" | "editor";

const SURFACES: ReadonlyArray<{ id: MobileSurface; label: string }> = [
  { id: "inbox", label: "Sessions" },
  { id: "terminal:claude", label: "Agent" },
  { id: "terminal:shell", label: "Shell" },
  { id: "editor", label: "Code" },
];

/** Compact navigation. Swipes are scoped to the bar so editor and terminal gestures remain untouched. */
export function MobileSurfaceBar(props: {
  active: MobileSurface;
  onSelect: (surface: MobileSurface) => void;
  titleOf: (surface: MobileSurface, label: string) => string;
}): JSX.Element {
  let startX: number | null = null;
  let swiped = false;

  const step = (delta: number): void => {
    const current = SURFACES.findIndex((surface) => surface.id === props.active);
    props.onSelect(SURFACES[(current + delta + SURFACES.length) % SURFACES.length]!.id);
  };

  return (
    <nav
      class="mobile-surface-bar"
      aria-label="Workspace surfaces"
      tabIndex={-1}
      onPointerDown={(event) => {
        startX = event.clientX;
        swiped = false;
      }}
      onPointerUp={(event) => {
        if (startX === null) {
          return;
        }
        const distance = event.clientX - startX;
        startX = null;
        if (Math.abs(distance) >= 48) {
          swiped = true;
          event.currentTarget.focus({ preventScroll: true });
          step(distance < 0 ? 1 : -1);
        }
      }}
      onPointerCancel={() => {
        startX = null;
      }}
    >
      <For each={SURFACES}>
        {(surface) => (
          <button
            type="button"
            class="mobile-surface-button"
            classList={{ active: props.active === surface.id }}
            aria-current={props.active === surface.id ? "page" : undefined}
            title={props.titleOf(surface.id, surface.label)}
            onClick={(event) => {
              const suppressPointerClick = swiped && event.detail !== 0;
              swiped = false;
              if (!suppressPointerClick) {
                event.currentTarget.focus({ preventScroll: true });
                props.onSelect(surface.id);
              }
            }}
          >
            {surface.label}
          </button>
        )}
      </For>
    </nav>
  );
}
