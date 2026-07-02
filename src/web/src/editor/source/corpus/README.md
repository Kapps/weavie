# Notion enhanced-markdown golden corpus

Fixture pages for the renderer's snapshot tests (`notion-markdown.test.ts`). `complete-example.md` is the
official guide's "Complete example" verbatim; `kitchen-sink.md` covers every documented block + inline form;
`real-page.md` is a captured `GET /pages/{id}/markdown` body. Grow this corpus from REAL API responses (fetch a
scratch page's markdown and drop it here) — never trim fixtures to what the renderer already handles.
