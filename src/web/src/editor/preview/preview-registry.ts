// Which file types have a rendered Preview. Markdown-only today; adding image / video / HTML later is a
// matter of extending PREVIEWABLE here and teaching PreviewPane to render the new type. Deliberately free of
// the markdown renderer (and its libraries) so the shell can cheaply ask "is this previewable?" without
// pulling the preview chunk onto the first-paint path.
const PREVIEWABLE = new Set(["md", "markdown"]);

function extensionOf(path: string): string {
  const name = path.split(/[\\/]/).pop() ?? path;
  const dot = name.lastIndexOf(".");
  return dot === -1 ? "" : name.slice(dot + 1).toLowerCase();
}

/// Whether `path` has a Preview mode — drives the toggle affordance, the command, and the re-press gesture.
export function canPreview(path: string): boolean {
  return PREVIEWABLE.has(extensionOf(path));
}
