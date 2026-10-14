import { createMarkdownRenderer } from "./markdown-renderer";

export const renderMarkdown = createMarkdownRenderer({
  html: "any",
  allowImages: true,
  allowMermaid: true,
  safeLinksOnly: false,
});
