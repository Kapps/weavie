import { createMarkdownRenderer } from "./markdown-renderer";

export const renderMarkdown = createMarkdownRenderer({
  allowHtml: true,
  allowImages: true,
  allowMermaid: true,
  safeLinksOnly: false,
});
