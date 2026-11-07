import { createMarkdownEngine } from "./markdown-renderer";

export const previewEngine = createMarkdownEngine({
  html: "any",
  allowImages: true,
  allowMermaid: true,
  safeLinksOnly: false,
});
