import { type ClientSession, mediaResourceUrl, sessionResourceUrl } from "../bridge";
import { fileIndexFor } from "../files/session-files";
import { createSessionOwnedResource } from "../messaging/session-owned-state";
import { AGENT_IMAGE_HEIGHT } from "./AgentPaneEstimate";

// Images as same-origin object URLs, kept so a streaming or remounted reply never fetches or flickers again; a
// failure isn't kept, so the next render retries it.
const loaded = createSessionOwnedResource(
  () => ({ pending: new Map<string, Promise<string>>(), ready: new Map<string, string>() }),
  (_session, images) => {
    for (const url of images.ready.values()) URL.revokeObjectURL(url);
  },
);

/** Loads the `.agent-image` placeholders a hosted-image reply rendered; returns a function that stops it. */
export function hydrateAgentImages(root: HTMLElement, session: ClientSession): () => void {
  let live = true;
  for (const holder of root.querySelectorAll<HTMLElement>("span.agent-image[data-src]")) {
    holder.style.height = `${AGENT_IMAGE_HEIGHT}px`;
    const source = holder.dataset.src ?? "";
    const show = (url: string): void => {
      const image = document.createElement("img");
      image.alt = holder.dataset.alt ?? "";
      image.src = url;
      holder.replaceChildren(image);
    };
    const ready = loaded.get(session)?.ready.get(source);
    if (ready !== undefined) {
      show(ready);
      continue;
    }
    const status = document.createElement("span");
    status.className = "agent-image-status";
    status.setAttribute("role", "status");
    status.textContent = "Loading image…";
    holder.replaceChildren(status);
    load(session, source)
      .then((url) => {
        if (live) show(url);
      })
      .catch((error: unknown) => {
        if (live)
          status.textContent = `Image unavailable: ${error instanceof Error ? error.message : String(error)}`;
      });
  }
  return () => {
    live = false;
  };
}

/**
 * Crops `region`, in the image element's CSS pixels, out of a loaded agent image. The image is a same-origin object
 * URL, so the canvas is never tainted; `object-fit: contain` letterboxing is mapped back to the source pixels.
 */
export function cropAgentImage(
  image: HTMLImageElement,
  region: { x: number; y: number; width: number; height: number },
): Promise<Blob> {
  const scale = Math.min(
    image.clientWidth / image.naturalWidth,
    image.clientHeight / image.naturalHeight,
  );
  const left = (image.clientWidth - image.naturalWidth * scale) / 2;
  const top = (image.clientHeight - image.naturalHeight * scale) / 2;
  const x = Math.max(0, (region.x - left) / scale);
  const y = Math.max(0, (region.y - top) / scale);
  const width = Math.min(image.naturalWidth - x, region.width / scale);
  const height = Math.min(image.naturalHeight - y, region.height / scale);
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(width));
  canvas.height = Math.max(1, Math.round(height));
  canvas.getContext("2d")?.drawImage(image, x, y, width, height, 0, 0, canvas.width, canvas.height);
  return new Promise((resolve, reject) =>
    canvas.toBlob(
      (blob) => (blob === null ? reject(new Error("The crop is empty.")) : resolve(blob)),
      "image/png",
    ),
  );
}

function load(session: ClientSession, source: string): Promise<string> {
  const images = loaded.get(session);
  if (images === undefined) return Promise.reject(new Error("the session has closed."));
  const pending = images.pending.get(source);
  if (pending !== undefined) return pending;
  const image = fetchImage(session, source);
  images.pending.set(source, image);
  image.then(
    (url) => images.ready.set(source, url),
    () => {},
  );
  image.finally(() => images.pending.delete(source)).catch(() => {});
  return image;
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
  if (/^[A-Za-z][A-Za-z\d+.-]*:/.test(source) && !/^[A-Za-z]:[\\/]/.test(source)) {
    const url = sessionResourceUrl(session, "/weavie-agent-image");
    url.searchParams.set("url", source);
    return url.toString();
  }
  const path = decodeURI(source);
  const root = fileIndexFor(session).root;
  const absolute = /^([\\/]|[A-Za-z]:[\\/])/.test(path);
  if (!absolute && root === null) throw new Error("the workspace folder isn't known yet.");
  const media = mediaResourceUrl(
    session,
    absolute ? path : `${root}/${path.replace(/^\.\//, "")}`,
    0,
  );
  if (media === null) throw new Error("the session's media origin is unavailable.");
  return media;
}
