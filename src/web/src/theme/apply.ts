// Applies an effective color palette to Weavie's own chrome via CSS custom properties (spec §6: one of
// the three live-reapply surfaces, alongside Monaco and xterm). Each VS Code color id becomes a
// `--weavie-<dotted-key-as-dashes>` variable on :root, so chrome CSS can consume e.g.
// `var(--weavie-statusBar-background)`. Cheap and idempotent — safe to call on every override change.

/** Sets a `--weavie-*` CSS variable on :root for each color id in <paramref name="colors"/>. */
export function applyColorsToCssVars(colors: Readonly<Record<string, string>>): void {
  const root = document.documentElement;
  for (const [key, value] of Object.entries(colors)) {
    root.style.setProperty(cssVarName(key), value);
  }
}

/** Maps a VS Code color id (e.g. <c>statusBar.background</c>) to its CSS var (<c>--weavie-statusBar-background</c>). */
export function cssVarName(colorId: string): string {
  return `--weavie-${colorId.replaceAll(".", "-")}`;
}
