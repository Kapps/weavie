import type { JSX } from "solid-js";

/**
 * The web-tab surface: an iframe filling the editor pane, overlaying the (kept-mounted) Monaco host exactly as
 * the Markdown Preview overlay does. Used to preview a local dev server or any http(s) page in a tab. Sites that
 * forbid framing (X-Frame-Options / CSP frame-ancestors) won't load — the intended use is previewing your own app.
 */
export default function WebTabPane(props: { url: () => string }): JSX.Element {
  return (
    <div class="editor-web" data-kind="editor">
      <iframe class="editor-web-frame" src={props.url()} title="Web preview" />
    </div>
  );
}
