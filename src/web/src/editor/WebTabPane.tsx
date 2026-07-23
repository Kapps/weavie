import type { JSX } from "solid-js";

/**
 * The web-tab surface: an iframe over the (kept-mounted) Monaco host, like the Markdown preview overlay. For
 * previewing an http(s) page (e.g. a local dev server); sites that forbid framing won't load. The sandbox
 * deliberately omits allow-top-navigation: a framed page can browse freely inside its frame but can never
 * replace Weavie itself; its popups route through the host's new-window policy.
 */
export default function WebTabPane(props: { url: () => string }): JSX.Element {
  return (
    <div class="editor-web" data-kind="editor">
      <iframe
        class="editor-web-frame"
        src={props.url()}
        title="Web preview"
        sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-downloads allow-modals"
      />
    </div>
  );
}
