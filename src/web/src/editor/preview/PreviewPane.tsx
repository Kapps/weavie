import { createEffect, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { ClientSession } from "../../bridge";
import { installContentNavigation } from "../../content-navigation";
import { onPreviewThemeChanged } from "../../theme/controller";
import { basename } from "../fs-path";
import {
  isChangesOnly,
  markdownChangeMarks,
  previewScroll,
  rememberPreviewScroll,
  setViewMode,
  toggleChangesOnly,
} from "../view-mode-store";
import { BlockEditor } from "./block-editor";
import { PreviewReview } from "./changes/preview-review";
import { installEmbedZoomAndMermaid } from "./embed-zoom";
import HtmlPreview from "./HtmlPreview";
import type { PreviewDocument } from "./preview-document";
import { registerPreviewPane } from "./preview-panes";
import { previewKindOf } from "./preview-registry";

// Rendered-file overlay shown over the (still-mounted) Monaco host when the active file is in Preview mode.
// Reads the live working-copy text through `content`, so the render tracks edits and reloads without a
// re-toggle. SVG uses an <img>-hosted Blob URL so workspace scripts and styles never enter the app document.
export default function PreviewPane(props: PreviewProps): JSX.Element {
  return (
    <Show when={previewKindOf(props.path()) === "html"} fallback={<DocumentPreview {...props} />}>
      <HtmlPreview {...props} />
    </Show>
  );
}

export interface PreviewProps {
  session: () => ClientSession;
  path: () => string;
  content: () => string;
  document: PreviewDocument;
  /** Registers what runs once the tab is placed, with the line an open targeted (undefined when none). */
  onPlaced(handler: (line: number | undefined) => void): void;
  bind(element: HTMLElement): () => void;
}

function DocumentPreview(props: PreviewProps): JSX.Element {
  let host!: HTMLDivElement;
  let body!: HTMLDivElement;
  let svgUrl: string | undefined;
  let blocks: BlockEditor | undefined;
  let review: PreviewReview | undefined;
  // Each render bumps the generation; hydrateMermaid checks it after every await so an async diagram render
  // that resolves after a newer edit (or theme switch) is dropped instead of landing in stale DOM.
  let generation = 0;

  const releaseSvg = (): void => {
    if (svgUrl !== undefined) {
      URL.revokeObjectURL(svgUrl);
      svgUrl = undefined;
    }
  };

  const render = (rebuild: boolean): void => {
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
    blocks?.render(props.content(), rebuild);
  };
  const hydrate = (): void => {
    const gen = generation;
    installEmbedZoomAndMermaid(body, props.session(), () => gen === generation);
  };

  const rendered = (): void => {
    hydrate();
    review?.paint();
  };

  onMount(() => {
    if (previewKindOf(props.path()) === "markdown") {
      const editor = new BlockEditor(
        host,
        body,
        props.document,
        {
          clearMarks: (unit) => review?.clearMarks(unit),
          originalOf: (line) => review?.originalOf(line),
        },
        rendered,
      );
      review = new PreviewReview(editor.list, host, body, props.document, {
        showSource: () => setViewMode(props.session(), props.path(), "source"),
        toggleChangesOnly: () => toggleChangesOnly(props.session(), props.path()),
        changesOnly: () => isChangesOnly(props.session(), props.path()),
        marksAll: () => markdownChangeMarks() === "all",
        editing: () => editor.editing,
      });
      blocks = editor;
      onCleanup(registerPreviewPane({ host, editor, review, uri: props.document.uri }));
      createEffect(() => {
        isChangesOnly(props.session(), props.path());
        markdownChangeMarks();
        review?.paint();
      });
    }
    props.onPlaced((line) => {
      if (line !== undefined) blocks?.reveal(line - 1);
      else host.scrollTop = previewScroll(props.session(), props.path());
    });
    createEffect(() => render(false));
    onCleanup(() => {
      rememberPreviewScroll(props.session(), props.path(), host.scrollTop);
      props.onPlaced(() => {});
      review?.clear();
      blocks?.dispose();
    });
  });
  onMount(() => onCleanup(installContentNavigation(body, props.session, props.path)));
  onMount(() => onCleanup(props.bind(host)));
  // Mermaid bakes the theme into its SVG, so re-render diagrams when the active theme switches.
  const unsubscribeTheme = onPreviewThemeChanged(() => {
    if (previewKindOf(props.path()) === "markdown") {
      render(true);
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
