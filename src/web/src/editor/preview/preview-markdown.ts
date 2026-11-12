import { createMarkdownEngine } from "./markdown-renderer";

export const previewEngine = createMarkdownEngine({
  html: "any",
  images: "direct",
  allowMermaid: true,
  safeLinksOnly: false,
});
