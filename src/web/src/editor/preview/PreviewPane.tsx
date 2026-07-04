import { createEffect, type JSX, onCleanup, onMount } from "solid-js";
import { onPreviewThemeChanged } from "../../theme/controller";
import { installEmbedZoomAndMermaid } from "./embed-zoom";
import { renderMarkdown } from "./preview-markdown";

// Rendered-Markdown overlay shown over the (still-mounted) Monaco host when the active file is in Preview
// mode. Reads the live working-copy text through `content` — a reactive signal off the editor model — so the
// render tracks edits / reloads without a re-toggle. Sits inside .editor-surface (data-kind="editor"), so
// focus tracking still counts it as the editor pane; replaceChildren on the inner body keeps the outer
// scroll container across re-renders.
export default function PreviewPane(props: { content: () => string }): JSX.Element {
  let host!: HTMLDivElement;
  let body!: HTMLDivElement;
  // Each render bumps the generation; hydrateMermaid checks it after every await so an async diagram render
  // that resolves after a newer edit (or theme switch) is dropped instead of landing in stale DOM.
  let generation = 0;

  const render = (): void => {
    generation += 1;
    const gen = generation;
    body.replaceChildren(renderMarkdown(props.content()));
    installEmbedZoomAndMermaid(body, () => gen === generation);
  };

  createEffect(render);
  onMount(() => host.focus());
  // Mermaid bakes the theme into its SVG, so re-render diagrams when the active theme switches.
  onCleanup(onPreviewThemeChanged(render));

  return (
    <div class="editor-preview" data-kind="editor" tabindex="0" ref={host}>
      <div class="editor-preview-body" ref={body} />
    </div>
  );
}
