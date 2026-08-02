export interface PopoverAnchor {
  left: number;
  right: number;
  top: number;
  bottom: number;
}

export interface PopoverSize {
  width: number;
  height: number;
}

export interface PopoverPosition {
  left: number;
  top: number;
}

const INSET = 8;
const GAP = 6;

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), Math.max(min, max));
}

/** Places a rail popover beside its anchor while keeping every edge inside the viewport. */
export function placeRailPopover(
  anchor: PopoverAnchor,
  panel: PopoverSize,
  viewport: PopoverSize,
): PopoverPosition {
  const right = anchor.right + GAP;
  const left = anchor.left - GAP - panel.width;
  const maxLeft = viewport.width - panel.width - INSET;
  const x =
    right + panel.width <= viewport.width - INSET
      ? right
      : left >= INSET
        ? left
        : clamp(right, INSET, maxLeft);

  const bottomAligned = anchor.bottom - panel.height;
  const topAligned = anchor.top;
  const maxTop = viewport.height - panel.height - INSET;
  const y =
    bottomAligned >= INSET
      ? bottomAligned
      : topAligned >= INSET && topAligned + panel.height <= viewport.height - INSET
        ? topAligned
        : clamp(bottomAligned, INSET, maxTop);

  return { left: x, top: y };
}
