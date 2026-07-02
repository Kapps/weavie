// Which file types have a rendered Preview. Markdown-only today; adding HTML later is a matter of extending
// PREVIEWABLE here and teaching PreviewPane to render the new type (images/video render in the media pane
// instead — see media/media-types.ts). Deliberately free of the markdown renderer (and its libraries) so the
// shell can cheaply ask "is this previewable?" without pulling the preview chunk onto the first-paint path.
import { extensionOf } from "../fs-path";

const PREVIEWABLE = new Set(["md", "markdown"]);

/// Whether `path` has a Preview mode — drives the toggle affordance, the command, and the re-press gesture.
export function canPreview(path: string): boolean {
  return PREVIEWABLE.has(extensionOf(path));
}
