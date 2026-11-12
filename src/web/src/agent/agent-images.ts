import { type ClientSession, mediaResourceUrl, sessionResourceUrl } from "../bridge";
import { samePath } from "../editor/fs-path";
import { fileIndexFor } from "../files/session-files";
import { createSessionOwnedResource } from "../messaging/session-owned-state";
import { AGENT_IMAGE_HEIGHT } from "./AgentPaneEstimate";
import { cropPng, type Rect, sourceRect } from "./agent-crop";

// Each image loads once per session as a same-origin object URL, so a streaming or remounted reply never fetches it
// again; a workspace file that changes is fetched again by the next reply that shows it. A failure is kept only for
// the mounted reply that saw it, so its streamed re-renders don't refetch, while a new or remounted reply retries.
interface SessionImages {
  outcomes: Map<string, Promise<string>>;
  ready: Map<string, string>;
  off: () => void;
}
const failures = new WeakMap<HTMLElement, Map<string, string>>();
const loaded = createSessionOwnedResource<SessionImages>(
  (session) => {
    const outcomes = new Map<string, Promise<string>>();
    const ready = new Map<string, string>();
    const off = session
      .feature("files")
      .on<{ changes: { path: string }[] }>("changed", ({ changes }) => {
        for (const source of [...outcomes.keys()]) {
          const path = workspacePath(session, source);
          if (path === null || !changes.some((change) => samePath(change.path, path))) continue;
          outcomes.delete(source);
          const url = ready.get(source);
          if (url !== undefined) URL.revokeObjectURL(url);
          ready.delete(source);
        }
      });
    return { outcomes, ready, off };
  },
  (_session, images) => {
    images.off();
    for (const url of images.ready.values()) URL.revokeObjectURL(url);
  },
);

/** Loads the `.agent-image` placeholders a hosted-image reply rendered; returns a function that stops it. */
export function hydrateAgentImages(root: HTMLElement, session: ClientSession): () => void {
  let live = true;
  const images = loaded.get(session);
  if (images === undefined) return () => {};
  let failed = failures.get(root);
  if (failed === undefined) {
    failed = new Map();
    failures.set(root, failed);
  }
  const seen = failed;
  for (const holder of root.querySelectorAll<HTMLElement>("span.agent-image[data-src]")) {
    holder.style.height = `${AGENT_IMAGE_HEIGHT}px`;
    const source = holder.dataset.src ?? "";
    const show = (url: string): void => {
      const image = document.createElement("img");
      image.alt = holder.dataset.alt ?? "";
      image.src = url;
      holder.replaceChildren(image);
    };
    const ready = images.ready.get(source);
    if (ready !== undefined) {
      show(ready);
      continue;
    }
    const status = document.createElement("span");
    status.className = "agent-image-status";
    status.setAttribute("role", "status");
    status.textContent = "Loading image…";
    holder.replaceChildren(status);
    // Not an outcome to keep: the next render, once the root is known, loads it.
    if (!isRemote(source) && workspacePath(session, source) === null) {
      status.textContent = "Image unavailable: the workspace folder isn't known yet.";
      continue;
    }
    const reason = seen.get(source);
    if (reason !== undefined) {
      status.textContent = reason;
      continue;
    }
    let outcome = images.outcomes.get(source);
    if (outcome === undefined) {
      const fetched = fetchImage(session, source);
      images.outcomes.set(source, fetched);
      fetched.then(
        (url) => {
          if (images.outcomes.get(source) === fetched) images.ready.set(source, url);
        },
        () => {
          if (images.outcomes.get(source) === fetched) images.outcomes.delete(source);
        },
      );
      outcome = fetched;
    }
    outcome
      .then((url) => {
        if (live) show(url);
      })
      .catch((error: unknown) => {
        const message = `Image unavailable: ${error instanceof Error ? error.message : String(error)}`;
        seen.set(source, message);
        if (live) status.textContent = message;
      });
  }
  return () => {
    live = false;
  };
}

/** How a loaded agent image is drawn in its frame, measured while it is on screen. */
export interface ImageFrame {
  width: number;
  height: number;
  position: { x: number; y: number };
}

export function imageFrame(image: HTMLImageElement): ImageFrame {
  const [x = 50, y = 50] = getComputedStyle(image).objectPosition.split(" ").map(Number.parseFloat);
  return {
    width: image.clientWidth,
    height: image.clientHeight,
    position: { x: x / 100, y: y / 100 },
  };
}

/**
 * Crops `region`, in `frame`'s CSS pixels, out of a loaded agent image. The frame is measured when the gesture
 * starts, so a reply re-rendering mid-drag can't change it; a same-origin URL never taints the canvas.
 */
export function cropAgentImage(
  image: HTMLImageElement,
  frame: ImageFrame,
  region: Rect,
): Promise<Blob> {
  const area = sourceRect(
    frame,
    { width: image.naturalWidth, height: image.naturalHeight },
    frame.position,
    region,
  );
  return area === null
    ? Promise.reject(new Error("The box is outside the image."))
    : cropPng(image, area);
}

async function fetchImage(session: ClientSession, source: string): Promise<string> {
  const response = await fetch(imageUrl(session, source));
  if (!response.ok) {
    throw new Error((await response.text()).trim() || `the host answered ${response.status}.`);
  }
  return URL.createObjectURL(await response.blob());
}

// A URL with a scheme goes to the host, which loads only https images; anything else is a workspace path.
function imageUrl(session: ClientSession, source: string): string {
  const path = workspacePath(session, source);
  if (path === null) {
    const url = sessionResourceUrl(session, "/weavie-agent-image");
    url.searchParams.set("url", source);
    return url.toString();
  }
  const media = mediaResourceUrl(session, path, 0);
  if (media === null) throw new Error("the session's media origin is unavailable.");
  return media;
}

function isRemote(source: string): boolean {
  return /^[A-Za-z][A-Za-z\d+.-]*:/.test(source) && !/^[A-Za-z]:[\\/]/.test(source);
}

// The absolute workspace path a source names, or null for a remote address or before the root is known.
function workspacePath(session: ClientSession, source: string): string | null {
  if (isRemote(source)) return null;
  const path = decodeURI(source);
  if (/^([\\/]|[A-Za-z]:[\\/])/.test(path)) return path;
  const root = fileIndexFor(session).root;
  return root === null ? null : `${root}/${path.replace(/^\.\//, "")}`;
}
