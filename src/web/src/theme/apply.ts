// Applies a color palette to Weavie's chrome via CSS custom properties (spec §6: one of the three
// live-reapply surfaces). Each VS Code color id becomes a `--weavie-<dotted-key-as-dashes>` var on :root
// (e.g. `var(--weavie-statusBar-background)`). Idempotent.

/** Sets a `--weavie-*` CSS variable on :root for each color id in `colors`. */
export function applyColorsToCssVars(colors: Readonly<Record<string, string>>): void {
  const root = document.documentElement;
  for (const [key, value] of Object.entries(colors)) {
    root.style.setProperty(cssVarName(key), value);
  }
}

/** Maps a VS Code color id (e.g. `statusBar.background`) to its CSS var (`--weavie-statusBar-background`). */
export function cssVarName(colorId: string): string {
  return `--weavie-${colorId.replaceAll(".", "-")}`;
}
