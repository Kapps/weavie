import type { ClientSession } from "../bridge";

const ASSET_ID = "[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}";
const GITHUB_ASSET = new RegExp(
  `^/(?:user-attachments/assets|[^/]+/[^/]+/assets/\\d+)/${ASSET_ID}$`,
  "i",
);

export function isGithubMediaUrl(href: string): boolean {
  try {
    const url = new URL(href);
    if (url.protocol !== "https:" || url.port || url.username || url.password) return false;
    return (
      (url.hostname === "github.com" && GITHUB_ASSET.test(url.pathname)) ||
      (url.hostname === "user-images.githubusercontent.com" && /^\/\d+\/[^/]+$/.test(url.pathname))
    );
  } catch {
    return false;
  }
}

type AttachmentKind = "image" | "video" | "unsupported";

/** Asks the host what each attachment is: WebKit hangs decoding a video given to an <img>, and plays images as video. */
export function hydrateGithubMedia(root: HTMLElement, session: ClientSession): () => void {
  const lifetime = new AbortController();
  const media: (HTMLImageElement | HTMLVideoElement)[] = [];
  for (const anchor of root.querySelectorAll<HTMLAnchorElement>("a[href]")) {
    const href = anchor.getAttribute("href") ?? "";
    if (
      !isGithubMediaUrl(href) ||
      anchor.closest("pre, code") !== null ||
      anchor.querySelector("img") !== null
    ) {
      continue;
    }
    const holder = document.createElement("span");
    holder.className = "github-media";
    const status = document.createElement("span");
    status.className = "github-media-status";
    status.setAttribute("role", "status");
    status.textContent = "Loading attachment preview…";
    holder.append(status);
    anchor.after(holder);
    const unavailable = (): void => {
      status.textContent = "Attachment preview unavailable: unsupported media or failed to load.";
      holder.replaceChildren(status);
    };
    session
      .feature("pullRequests")
      .request<{ kind: AttachmentKind }, { url: string }>(
        "attachmentKind",
        { url: href },
        lifetime.signal,
      )
      .then(({ kind }) => {
        if (lifetime.signal.aborted) return;
        if (kind === "unsupported") return unavailable();
        const element = kind === "video" ? videoFor(anchor) : imageFor(anchor);
        media.push(element);
        element.onerror = () => {
          release(element);
          unavailable();
        };
        element.src = href;
        holder.replaceChildren(element);
      })
      .catch(() => {
        if (!lifetime.signal.aborted) unavailable();
      });
  }
  return () => {
    lifetime.abort();
    for (const element of media) release(element);
  };
}

const label = (anchor: HTMLAnchorElement): string =>
  anchor.textContent?.trim() || "GitHub attachment";

function imageFor(anchor: HTMLAnchorElement): HTMLImageElement {
  const image = document.createElement("img");
  image.alt = label(anchor);
  return image;
}

function videoFor(anchor: HTMLAnchorElement): HTMLVideoElement {
  const video = document.createElement("video");
  video.controls = true;
  video.preload = "metadata";
  video.playsInline = true;
  video.setAttribute("aria-label", label(anchor));
  return video;
}

function release(media: HTMLImageElement | HTMLVideoElement): void {
  media.onerror = null;
  media.removeAttribute("src");
  if (media instanceof HTMLVideoElement) {
    media.pause();
    media.load();
  }
}
