// The host stylesheet injected into the SourceView shadow root. It owns the structural look of a rendered source
// doc (Notion); the mapper emits semantic markup with only known class names. Colors come from the app's theme
// custom properties (--fg/--bg/--accent/--border/--dim), which pierce the shadow boundary, so the content
// restyles live when Weavie toggles dark/light — no re-render. Notion text/background colors map to fixed,
// theme-neutral tints.
export const SOURCE_STYLES = `
.wv-source {
  background: var(--bg, #000000);
  color: var(--fg, #cdd5dc);
  font-family: var(--ui-font, system-ui, sans-serif);
  /* Crisp grayscale AA, matching the editor/terminal — without it the WebView's default subpixel
     smoothing renders this prose noticeably heavier/fuzzier than the rest of the app. */
  -webkit-font-smoothing: antialiased;
  -moz-osx-font-smoothing: grayscale;
  line-height: 1.6;
  font-size: 15px;
  min-height: 100%;
  padding: 2.5rem max(2rem, calc(50% - 22rem));
  box-sizing: border-box;
}
.wv-source > :first-child { margin-top: 0; }
.wv-source .wv-header { margin-bottom: 1.6rem; }
.wv-source .wv-title { font-size: 2.4em; font-weight: 800; line-height: 1.15; margin: 0 0 0.15em; }
.wv-source .wv-meta { color: var(--dim, #6f7884); font-size: 0.85em; }
.wv-source .wv-incomplete {
  color: var(--dim, #6f7884); font-size: 0.9em; margin: 0 0 1.2em;
  border: 1px solid var(--border, #191c21); border-radius: 8px; padding: 0.5em 0.8em;
  background: color-mix(in srgb, var(--fg, #cdd5dc) 6%, transparent);
}
/* Click-to-edit affordances (source-edit.ts): a subtle hover/focus tint on editable blocks, and the inline
   block editor that swaps in — same type metrics as the prose so the swap doesn't jump the layout. */
.wv-source .wv-editable { border-radius: 4px; cursor: text; }
.wv-source .wv-editable:hover,
.wv-source .wv-editable:focus-visible {
  background: color-mix(in srgb, var(--accent, #54c6a4) 8%, transparent);
  outline: none;
}
.wv-source .wv-editor-box { margin: 0.5em 0; }
.wv-source .wv-block-editor {
  display: block; width: 100%; box-sizing: border-box; resize: none; overflow: hidden;
  font: inherit; line-height: inherit; color: inherit;
  background: color-mix(in srgb, var(--fg, #cdd5dc) 6%, transparent);
  border: 1px solid var(--accent, #54c6a4); border-radius: 4px; padding: 0.25em 0.4em;
}
.wv-source .wv-block-editor:focus { outline: none; }
.wv-source .wv-saving .wv-block-editor { opacity: 0.6; }
.wv-source .wv-edit-hint { color: var(--dim, #6f7884); font-size: 0.78em; margin-top: 0.25em; }
.wv-source .wv-edit-error { color: var(--bad, #e07a7a); font-size: 0.85em; margin-top: 0.35em; }
.wv-source .wv-edit-refetch {
  display: block; margin-top: 0.3em; font: inherit; font-size: 0.95em; cursor: pointer;
  color: var(--accent, #54c6a4); background: none; border: 1px solid var(--border, #191c21);
  border-radius: 4px; padding: 0.15em 0.6em;
}
.wv-source .wv-content > :first-child { margin-top: 0; }
.wv-source h1 { font-size: 1.9em; font-weight: 700; margin: 1.4em 0 0.4em; line-height: 1.2; }
.wv-source h2 { font-size: 1.5em; font-weight: 600; margin: 1.3em 0 0.3em; }
.wv-source h3 { font-size: 1.2em; font-weight: 600; margin: 1.2em 0 0.3em; }
.wv-source p { margin: 0.5em 0; }
.wv-source a { color: var(--accent, #54c6a4); text-decoration: none; }
.wv-source a:hover { text-decoration: underline; }
.wv-source strong { font-weight: 700; }
.wv-source code {
  font-family: ui-monospace, "Cascadia Code", Menlo, monospace; font-size: 0.88em;
  background: color-mix(in srgb, var(--fg, #cdd5dc) 12%, transparent); padding: 0.1em 0.35em; border-radius: 4px;
}
.wv-source pre {
  background: color-mix(in srgb, var(--fg, #cdd5dc) 8%, transparent); border: 1px solid var(--border, #191c21);
  border-radius: 8px; padding: 0.9em 1em; overflow: auto; margin: 0.8em 0;
}
.wv-source pre code { background: none; padding: 0; font-size: 0.86em; }
.wv-source ul, .wv-source ol { margin: 0.5em 0; padding-left: 1.5em; }
.wv-source li { margin: 0.2em 0; }
.wv-source ul.wv-todos { list-style: none; padding-left: 0.2em; }
.wv-source li.wv-todo { display: flex; flex-wrap: wrap; align-items: baseline; gap: 0.5em; }
.wv-source li.wv-todo input { flex: none; accent-color: var(--accent, #54c6a4); }
.wv-source li.wv-todo .wv-children { flex-basis: 100%; }
/* A block's tab-indented child blocks, nested under any parent kind. */
.wv-source .wv-children { margin-left: 1.4em; }
.wv-source .wv-equation {
  margin: 0.9em 0; padding: 0.7em 1em; text-align: center; border-radius: 6px;
  font-family: ui-monospace, monospace; font-size: 0.92em;
  background: color-mix(in srgb, var(--fg, #cdd5dc) 6%, transparent);
}
.wv-source .wv-math { background: none; color: var(--accent, #54c6a4); }
.wv-source .wv-toc { margin: 0.8em 0; }
.wv-source .wv-toc ul { list-style: none; padding-left: 0; margin: 0; }
.wv-source .wv-toc a { color: var(--dim, #6f7884); text-decoration: underline; text-underline-offset: 3px; }
.wv-source .wv-toc a:hover { color: var(--accent, #54c6a4); }
.wv-source .wv-toc-l2 { padding-left: 1.2em; }
.wv-source .wv-toc-l3, .wv-source .wv-toc-l4, .wv-source .wv-toc-l5, .wv-source .wv-toc-l6 { padding-left: 2.4em; }
.wv-source blockquote {
  margin: 0.8em 0; padding: 0.1em 0 0.1em 1em; border-left: 3px solid var(--dim, #6f7884); color: var(--fg, #cdd5dc);
}
.wv-source hr { border: none; border-top: 1px solid var(--border, #191c21); margin: 1.4em 0; }
.wv-source .wv-callout {
  display: flex; gap: 0.7em; margin: 0.8em 0; padding: 0.9em 1em; border-radius: 8px;
  background: color-mix(in srgb, var(--fg, #cdd5dc) 10%, transparent);
}
.wv-source .wv-callout .wv-icon { flex: none; font-size: 1.1em; line-height: 1.4; }
.wv-source .wv-callout-body > :first-child { margin-top: 0; }
.wv-source .wv-callout-body > :last-child { margin-bottom: 0; }
.wv-source img { max-width: 100%; height: auto; border-radius: 6px; }
.wv-source details {
  margin: 0.5em 0; padding: 0.2em 0 0.2em 0.2em; border-left: 2px solid var(--border, #191c21);
}
.wv-source summary { cursor: pointer; font-weight: 500; }
.wv-source details > :not(summary) { margin-left: 1.2em; }
/* A toggleable heading reads as a heading with a disclosure marker — the heading sits inline, no toggle-block border. */
.wv-source .wv-toggle-heading { border-left: none; padding-left: 0; }
.wv-source summary > h1, .wv-source summary > h2, .wv-source summary > h3, .wv-source summary > h4 { display: inline; margin: 0; }
.wv-source .wv-underline { text-decoration: underline; }
.wv-source table { border-collapse: collapse; margin: 0.9em 0; width: 100%; font-size: 0.92em; }
.wv-source th, .wv-source td { border: 1px solid var(--border, #191c21); padding: 0.4em 0.7em; text-align: left; }
.wv-source th { background: color-mix(in srgb, var(--fg, #cdd5dc) 8%, transparent); font-weight: 600; }
.wv-source .wv-columns { display: flex; gap: 1.5em; margin: 0.8em 0; }
.wv-source .wv-column { flex: 1 1 0; min-width: 0; }
.wv-source .wv-card {
  display: inline-block; margin: 0.6em 0; padding: 0.6em 0.9em; border: 1px solid var(--border, #191c21);
  border-radius: 8px; color: var(--fg, #cdd5dc);
}
.wv-source pre.mermaid-pending { color: var(--dim, #6f7884); }
.wv-source .mermaid-rendered { margin: 1em 0; text-align: center; }
.wv-source .mermaid-rendered svg { max-width: 100%; height: auto; }
.wv-source .mermaid-error { color: var(--bad, #e07a7a); white-space: pre-wrap; }
/* Scoped under .wv-source so a bg tint out-specifies .wv-source .wv-callout's neutral background (and wins by
   source order); these also color inline <span color> text and {color=…} blocks. */
.wv-source .wv-color-gray { color: #9b9b9b; } .wv-source .wv-color-brown { color: #ba856f; } .wv-source .wv-color-orange { color: #e0883d; }
.wv-source .wv-color-yellow { color: #dfab01; } .wv-source .wv-color-green { color: #4dab9a; } .wv-source .wv-color-blue { color: #529cca; }
.wv-source .wv-color-purple { color: #9a6dd7; } .wv-source .wv-color-pink { color: #e255a1; } .wv-source .wv-color-red { color: #e0584b; }
.wv-source .wv-bg-gray { background: rgba(155,155,155,0.24); } .wv-source .wv-bg-brown { background: rgba(186,133,111,0.24); }
.wv-source .wv-bg-orange { background: rgba(224,136,61,0.24); } .wv-source .wv-bg-yellow { background: rgba(223,171,1,0.24); }
.wv-source .wv-bg-green { background: rgba(77,171,154,0.24); } .wv-source .wv-bg-blue { background: rgba(82,156,202,0.24); }
.wv-source .wv-bg-purple { background: rgba(154,109,215,0.24); } .wv-source .wv-bg-pink { background: rgba(226,85,161,0.24); }
.wv-source .wv-bg-red { background: rgba(224,88,75,0.24); }
/* The log viewer's dropped-lines marker (host-rendered html; see HostCore.Logs.cs). */
.wv-source .wv-logs-note { color: var(--dim, #6f7884); margin: 0.4em 0; }
.wv-status {
  display: flex; align-items: center; justify-content: center; gap: 0.6em;
  min-height: 60vh; color: var(--dim, #6f7884);
}
.wv-status.wv-error { color: var(--bad, #e07a7a); white-space: pre-wrap; text-align: center; }
.wv-spinner {
  width: 1.1em; height: 1.1em; border-radius: 50%; flex: none;
  border: 2px solid color-mix(in srgb, var(--fg, #cdd5dc) 22%, transparent);
  border-top-color: var(--accent, #54c6a4); animation: wv-spin 0.7s linear infinite;
}
@keyframes wv-spin { to { transform: rotate(360deg); } }
`;
