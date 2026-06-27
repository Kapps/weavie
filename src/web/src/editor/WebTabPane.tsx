import type { JSX } from "solid-js";

/**
 * The web-tab surface: an iframe over the (kept-mounted) Monaco host, like the Markdown preview overlay. For
 * previewing an http(s) page (e.g. a local dev server); sites that forbid framing won't load.
 */
export default function WebTabPane(props: { url: () => string }): JSX.Element {
  return (
    <div class="editor-web" data-kind="editor">
      <iframe class="editor-web-frame" src={props.url()} title="Web preview" />
    </div>
  );
}
