import { createEffect, type JSX, onCleanup, onMount } from "solid-js";
import { onPreviewThemeChanged } from "../../theme/controller";
import { basename } from "../fs-path";
import { installEmbedZoomAndMermaid } from "./embed-zoom";
import { renderMarkdown } from "./preview-markdown";
import { previewKindOf } from "./preview-registry";

// Rendered-file overlay shown over the (still-mounted) Monaco host when the active file is in Preview mode.
// Reads the live working-copy text through `content`, so the render tracks edits and reloads without a
// re-toggle. SVG uses an <img>-hosted Blob URL so workspace scripts and styles never enter the app document.
export default function PreviewPane(props: {
  path: () => string;
  content: () => string;
}): JSX.Element {
  let host!: HTMLDivElement;
  let body!: HTMLDivElement;
  let svgUrl: string | undefined;
  // Each render bumps the generation; hydrateMermaid checks it after every await so an async diagram render
  // that resolves after a newer edit (or theme switch) is dropped instead of landing in stale DOM.
  let generation = 0;

  const releaseSvg = (): void => {
    if (svgUrl !== undefined) {
      URL.revokeObjectURL(svgUrl);
      svgUrl = undefined;
    }
  };

  const render = (): void => {
    generation += 1;
    if (previewKindOf(props.path()) === "svg") {
      releaseSvg();
      body.classList.add("editor-preview-svg");
      const url = URL.createObjectURL(new Blob([props.content()], { type: "image/svg+xml" }));
      svgUrl = url;
      const image = document.createElement("img");
      image.alt = basename(props.path());
      image.addEventListener("error", () => {
        if (svgUrl !== url) {
          return;
        }
        const error = document.createElement("div");
        error.className = "editor-preview-error";
        error.textContent = `Could not render ${basename(props.path())} as SVG.`;
        body.replaceChildren(error);
      });
      image.src = url;
      body.replaceChildren(image);
      return;
    }

    releaseSvg();
    body.classList.remove("editor-preview-svg");
    const gen = generation;
    body.replaceChildren(renderMarkdown(props.content()));
    installEmbedZoomAndMermaid(body, () => gen === generation);
  };

  createEffect(render);
  onMount(() => host.focus());
  // Mermaid bakes the theme into its SVG, so re-render diagrams when the active theme switches.
  const unsubscribeTheme = onPreviewThemeChanged(() => {
    if (previewKindOf(props.path()) === "markdown") {
      render();
    }
  });
  onCleanup(() => {
    unsubscribeTheme();
    releaseSvg();
  });

  return (
    <div class="editor-preview" data-kind="editor" tabindex="0" ref={host}>
      <div class="editor-preview-body" ref={body} />
    </div>
  );
}
