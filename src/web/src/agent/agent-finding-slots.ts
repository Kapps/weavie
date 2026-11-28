import type MarkdownIt from "markdown-it";

const SLOT = /^[ \t]*\[\[([A-Za-z0-9._-]{1,64})\]\][ \t]*$/;

/**
 * A reply line that is only `[[id]]` becomes a slot where that finding is shown. It runs before inline parsing, so
 * an id such as `store.rs` is never read as a link.
 */
export function findingSlots(markdown: MarkdownIt): void {
  markdown.core.ruler.before("inline", "finding-slots", (state) => {
    const tokens: typeof state.tokens = [];
    for (let index = 0; index < state.tokens.length; index += 1) {
      const open = state.tokens[index]!;
      const inline = state.tokens[index + 1];
      const close = state.tokens[index + 2];
      const lines =
        open.type === "paragraph_open" &&
        inline?.type === "inline" &&
        close?.type === "paragraph_close"
          ? inline.content.split("\n")
          : [];
      if (inline === undefined || close === undefined || !lines.some((line) => SLOT.test(line))) {
        tokens.push(open);
        continue;
      }
      // The paragraph is split at its slot lines; the prose between them stays a paragraph.
      let prose: string[] = [];
      const flush = (): void => {
        if (prose.length === 0) return;
        const text = new state.Token("inline", "", 0);
        Object.assign(text, { content: prose.join("\n"), level: inline.level, children: [] });
        tokens.push(open, text, close);
        prose = [];
      };
      for (const line of lines) {
        const id = SLOT.exec(line)?.[1];
        if (id === undefined) {
          prose.push(line);
          continue;
        }
        flush();
        const slot = new state.Token("html_block", "", 0);
        slot.content = `<div class="agent-finding-slot" data-finding="${id}">[[${id}]]</div>\n`;
        tokens.push(slot);
      }
      flush();
      index += 2;
    }
    state.tokens = tokens;
  });
}
