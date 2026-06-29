import hljs from "highlight.js/lib/core";
import bash from "highlight.js/lib/languages/bash";
import c from "highlight.js/lib/languages/c";
import cpp from "highlight.js/lib/languages/cpp";
import csharp from "highlight.js/lib/languages/csharp";
import css from "highlight.js/lib/languages/css";
import go from "highlight.js/lib/languages/go";
import ini from "highlight.js/lib/languages/ini";
import java from "highlight.js/lib/languages/java";
import javascript from "highlight.js/lib/languages/javascript";
import json from "highlight.js/lib/languages/json";
import markdown from "highlight.js/lib/languages/markdown";
import python from "highlight.js/lib/languages/python";
import rust from "highlight.js/lib/languages/rust";
import sql from "highlight.js/lib/languages/sql";
import typescript from "highlight.js/lib/languages/typescript";
import xml from "highlight.js/lib/languages/xml";
import yaml from "highlight.js/lib/languages/yaml";

// Deliberate language subset (core build, not the auto-loading bundle) — the languages Weavie users actually
// preview. An unregistered language renders as escaped plain text, which is the correct no-highlight fallback.
for (const [name, lang] of Object.entries({
  bash,
  c,
  cpp,
  csharp,
  css,
  go,
  ini,
  java,
  javascript,
  json,
  markdown,
  python,
  rust,
  sql,
  typescript,
  xml,
  yaml,
})) {
  hljs.registerLanguage(name, lang);
}

/**
 * Highlights a fenced code block, returning a complete `&lt;pre class="hljs"&gt;…&lt;/pre&gt;` string of
 * `&lt;span class="hljs-*"&gt;` tokens (themed via CSS), or `""` for an unregistered language so markdown-it
 * falls back to its default escaped rendering.
 */
export function highlightFence(code: string, lang: string): string {
  const language = lang && hljs.getLanguage(lang) ? lang : "";
  if (language === "") {
    return "";
  }
  const { value } = hljs.highlight(code, { language });
  return `<pre class="hljs"><code class="language-${language}">${value}</code></pre>`;
}
