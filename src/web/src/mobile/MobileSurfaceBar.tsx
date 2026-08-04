import { For, type JSX } from "solid-js";

export type MobileSurface = "inbox" | "terminal:claude" | "terminal:shell" | "editor";

export type MobileSwipeDirection = -1 | 1;

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
  onSwipeCancel: () => void;
  onSwipeCommit: () => void;
  onSwipeProgress: (
    target: MobileSurface,
    direction: MobileSwipeDirection,
    progress: number,
  ) => void;
  titleOf: (surface: MobileSurface, label: string) => string;
}): JSX.Element {
  let start: { x: number; y: number } | null = null;
  let direction: MobileSwipeDirection | null = null;
  let swiped = false;

  const adjacent = (delta: MobileSwipeDirection): MobileSurface => {
    const current = SURFACES.findIndex((surface) => surface.id === props.active);
    return SURFACES[(current + delta + SURFACES.length) % SURFACES.length]!.id;
  };

  return (
    <nav
      class="mobile-surface-bar"
      aria-label="Workspace surfaces"
      tabIndex={-1}
      onPointerDown={(event) => {
        start = { x: event.clientX, y: event.clientY };
        direction = null;
        swiped = false;
        if (event.pointerId !== 0 && event.target instanceof Element) {
          event.target.setPointerCapture(event.pointerId);
        }
      }}
      onPointerMove={(event) => {
        if (start === null) {
          return;
        }
        const dx = event.clientX - start.x;
        const dy = event.clientY - start.y;
        if (direction === null) {
          if (Math.abs(dx) <= Math.abs(dy) || dx === 0) {
            return;
          }
          direction = dx < 0 ? 1 : -1;
        } else if (dx !== 0) {
          direction = dx < 0 ? 1 : -1;
        }
        props.onSwipeProgress(
          adjacent(direction),
          direction,
          Math.min(1, Math.abs(dx) / event.currentTarget.clientWidth),
        );
      }}
      onPointerUp={(event) => {
        if (start === null) {
          return;
        }
        const dx = event.clientX - start.x;
        const dy = event.clientY - start.y;
        start = null;
        if (Math.abs(dx) >= 48 && Math.abs(dx) > Math.abs(dy) * 1.5) {
          direction = dx < 0 ? 1 : -1;
          props.onSwipeProgress(
            adjacent(direction),
            direction,
            Math.min(1, Math.abs(dx) / event.currentTarget.clientWidth),
          );
          swiped = true;
          event.currentTarget.focus({ preventScroll: true });
          props.onSwipeCommit();
        } else {
          props.onSwipeCancel();
        }
        direction = null;
      }}
      onPointerCancel={() => {
        if (start !== null) {
          props.onSwipeCancel();
        }
        start = null;
        direction = null;
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
