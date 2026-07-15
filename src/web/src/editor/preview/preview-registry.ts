// Which file types have a rendered Preview. Images/video render in the media pane instead (see
// media/media-types.ts). Deliberately free of the renderers (and their libraries) so the shell can cheaply ask
// "is this previewable?" without pulling the preview chunk onto the first-paint path.
import { extensionOf } from "../fs-path";

export type PreviewKind = "markdown" | "svg";

const PREVIEWABLE = new Map<string, PreviewKind>([
  ["md", "markdown"],
  ["markdown", "markdown"],
  ["svg", "svg"],
]);

/// The renderer for `path`, or null when the file has no Preview mode.
export function previewKindOf(path: string): PreviewKind | null {
  return PREVIEWABLE.get(extensionOf(path)) ?? null;
}

/// Whether `path` has a Preview mode — drives the toggle affordance, the command, and the re-press gesture.
export function canPreview(path: string): boolean {
  return previewKindOf(path) !== null;
}
