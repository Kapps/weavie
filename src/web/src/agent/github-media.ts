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

/** Probes with native decoders: GitHub's extensionless redirects do not permit CORS MIME queries. */
export function hydrateGithubMedia(root: HTMLElement): () => void {
  const disposers: (() => void)[] = [];
  for (const anchor of root.querySelectorAll<HTMLAnchorElement>("a[href]")) {
    const href = anchor.getAttribute("href") ?? "";
    if (
      !isGithubMediaUrl(href) ||
      anchor.closest("pre, code") !== null ||
      anchor.querySelector("img") !== null
    ) {
      continue;
    }
    disposers.push(previewAttachment(anchor, href));
  }
  return () => {
    for (const dispose of disposers) dispose();
  };
}

function previewAttachment(anchor: HTMLAnchorElement, href: string): () => void {
  const holder = document.createElement("span");
  holder.className = "github-media";
  const status = document.createElement("span");
  status.className = "github-media-status";
  status.setAttribute("role", "status");
  status.textContent = "Loading attachment preview…";
  holder.append(status);
  anchor.after(holder);

  const image = document.createElement("img");
  image.alt = anchor.textContent?.trim() || "GitHub attachment";
  const video = document.createElement("video");
  video.controls = true;
  video.preload = "metadata";
  video.playsInline = true;
  video.setAttribute("aria-label", image.alt);
  let disposed = false;
  let selected: HTMLImageElement | HTMLVideoElement | undefined;
  const failed = new Set<Element>();

  const stop = (media: HTMLImageElement | HTMLVideoElement): void => {
    media.onload = null;
    media.onerror = null;
    if (media instanceof HTMLVideoElement) {
      media.onloadedmetadata = null;
      media.pause();
    }
    media.removeAttribute("src");
    if (media instanceof HTMLVideoElement) media.load();
  };
  const fail = (media: HTMLImageElement | HTMLVideoElement): void => {
    if (disposed) return;
    failed.add(media);
    stop(media);
    if (selected === media || (selected === undefined && failed.size === 2)) {
      status.textContent = "Attachment preview unavailable: unsupported media or failed to load.";
      holder.replaceChildren(status);
    }
  };
  const show = (media: HTMLImageElement | HTMLVideoElement): void => {
    if (disposed || selected !== undefined) return;
    selected = media;
    stop(media === image ? video : image);
    holder.replaceChildren(media);
  };
  image.onload = () => show(image);
  image.onerror = () => fail(image);
  video.onloadedmetadata = () => {
    if (video.videoWidth > 0 && video.videoHeight > 0) show(video);
    else fail(video);
  };
  video.onerror = () => fail(video);
  image.src = href;
  video.src = href;

  return () => {
    disposed = true;
    stop(image);
    stop(video);
  };
}
