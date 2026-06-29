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
  line-height: 1.6;
  font-size: 15px;
  min-height: 100%;
  padding: 2.5rem max(2rem, calc(50% - 22rem));
  box-sizing: border-box;
}
.wv-source > :first-child { margin-top: 0; }
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
.wv-source li.wv-todo { display: flex; align-items: baseline; gap: 0.5em; }
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
.wv-source img.wv-icon { width: 1.2em; height: 1.2em; object-fit: contain; }
.wv-source figure { margin: 1em 0; }
.wv-source figure img { max-width: 100%; height: auto; border-radius: 6px; display: block; }
.wv-source figcaption { color: var(--dim, #6f7884); font-size: 0.85em; margin-top: 0.4em; }
.wv-source details {
  margin: 0.5em 0; padding: 0.2em 0 0.2em 0.2em; border-left: 2px solid var(--border, #191c21);
}
.wv-source summary { cursor: pointer; font-weight: 500; }
.wv-source details > :not(summary) { margin-left: 1.2em; }
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
.wv-color-gray { color: #9b9b9b; } .wv-color-brown { color: #ba856f; } .wv-color-orange { color: #e0883d; }
.wv-color-yellow { color: #dfab01; } .wv-color-green { color: #4dab9a; } .wv-color-blue { color: #529cca; }
.wv-color-purple { color: #9a6dd7; } .wv-color-pink { color: #e255a1; } .wv-color-red { color: #e0584b; }
.wv-bg-gray { background: rgba(155,155,155,0.24); } .wv-bg-brown { background: rgba(186,133,111,0.24); }
.wv-bg-orange { background: rgba(224,136,61,0.24); } .wv-bg-yellow { background: rgba(223,171,1,0.24); }
.wv-bg-green { background: rgba(77,171,154,0.24); } .wv-bg-blue { background: rgba(82,156,202,0.24); }
.wv-bg-purple { background: rgba(154,109,215,0.24); } .wv-bg-pink { background: rgba(226,85,161,0.24); }
.wv-bg-red { background: rgba(224,88,75,0.24); }
`;
