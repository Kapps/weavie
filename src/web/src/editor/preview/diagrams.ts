import DOMPurify from "dompurify";

// Mermaid is hundreds of KB, so it's imported lazily — only a preview that actually contains a `mermaid`
// fence ever downloads it. The promise is memoized so the import + initialize happens once per session.
type Mermaid = typeof import("mermaid").default;
let mermaidPromise: Promise<Mermaid> | undefined;

function loadMermaid(): Promise<Mermaid> {
  if (mermaidPromise === undefined) {
    mermaidPromise = import("mermaid").then((mod) => mod.default);
  }
  return mermaidPromise;
}

let renderCount = 0;

/** Maps the active Weavie palette (live CSS vars) onto Mermaid's `base` theme so diagrams track the theme. */
function themeConfig(): Parameters<Mermaid["initialize"]>[0] {
  const css = getComputedStyle(document.documentElement);
  const v = (name: string): string => css.getPropertyValue(name).trim();
  return {
    startOnLoad: false,
    securityLevel: "strict",
    // SVG-native <text> labels, not HTML-in-<foreignObject> — the SVG-profile sanitize below strips
    // foreignObject, which silently deleted every node label.
    htmlLabels: false,
    flowchart: { htmlLabels: false },
    theme: "base",
    themeVariables: {
      darkMode: document.documentElement.dataset.themeType === "dark",
      background: v("--bg"),
      primaryColor: v("--bar"),
      primaryTextColor: v("--fg"),
      primaryBorderColor: v("--border"),
      secondaryColor: v("--bar"),
      tertiaryColor: v("--bg"),
      lineColor: v("--accent"),
      textColor: v("--fg"),
      fontFamily: "inherit",
    },
  };
}

/**
 * Fills every `pre.mermaid-pending` placeholder under `root` with rendered, sanitized SVG. `isCurrent`
 * is re-checked after each await so a render resolving after a newer edit (or theme switch) is discarded
 * instead of landing in stale DOM. A diagram that fails to parse surfaces its error in place.
 */
export async function hydrateMermaid(root: HTMLElement, isCurrent: () => boolean): Promise<void> {
  const pending = root.querySelectorAll<HTMLElement>("pre.mermaid-pending");
  if (pending.length === 0) {
    return;
  }
  const mermaid = await loadMermaid();
  if (!isCurrent()) {
    return;
  }
  mermaid.initialize(themeConfig());
  for (const node of pending) {
    const source = node.textContent ?? "";
    renderCount += 1;
    // mermaid.render names the output SVG with this id and cleans up the off-screen node it measured in,
    // so the id must not be reused to remove anything — doing so would delete the rendered SVG itself.
    const id = `weavie-mermaid-${renderCount}`;
    try {
      const { svg } = await mermaid.render(id, source);
      if (!isCurrent()) {
        return;
      }
      const wrapper = document.createElement("div");
      wrapper.className = "mermaid-rendered";
      // Mermaid SVG is generated, but the diagram source is untrusted file content — keep it inside the
      // same DOMPurify boundary as the rest of the preview, via the SVG profile.
      wrapper.innerHTML = DOMPurify.sanitize(svg, {
        USE_PROFILES: { svg: true, svgFilters: true },
      });
      node.replaceWith(wrapper);
    } catch (err) {
      if (!isCurrent()) {
        return;
      }
      const error = document.createElement("pre");
      error.className = "mermaid-error";
      error.textContent = err instanceof Error ? err.message : String(err);
      node.replaceWith(error);
    }
  }
}
