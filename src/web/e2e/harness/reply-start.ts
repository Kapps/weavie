import type { Locator } from "@playwright/test";

/**
 * A row's top, measured from where a reply read from its start rests in its agent pane: the start line, or as
 * low as the transcript above the row lets it sit.
 */
export const fromReplyStart = (row: Locator): Promise<number> =>
  row.evaluate((element) => {
    const body = element.closest(".agent-body")!;
    const top = element.getBoundingClientRect().top - body.getBoundingClientRect().top;
    const above = top + body.scrollHeight - body.clientHeight + body.scrollTop;
    const line = body.clientHeight * window.__WEAVIE_AGENT__!.replyStartOffset;
    return top - Math.min(line, above);
  });
