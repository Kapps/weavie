// Which file types render in the media pane (an <img>/<video> overlay) instead of a Monaco working copy —
// THE single source of truth for media detection. SVG is deliberately absent: it's editable text, and rendering it belongs to
// the Preview toggle (preview/preview-registry.ts).
import { extensionOf } from "../fs-path";

const IMAGE: Record<string, string> = {
  png: "image/png",
  jpg: "image/jpeg",
  jpeg: "image/jpeg",
  gif: "image/gif",
  webp: "image/webp",
  bmp: "image/bmp",
  ico: "image/x-icon",
  avif: "image/avif",
};

const VIDEO: Record<string, string> = {
  mp4: "video/mp4",
  m4v: "video/mp4",
  webm: "video/webm",
  mov: "video/quicktime",
  ogv: "video/ogg",
};

export interface MediaType {
  kind: "image" | "video";
  mime: string;
}

/// The media type of `path` (extension-matched), or null for everything Monaco should open as text.
export function mediaTypeOf(path: string): MediaType | null {
  const ext = extensionOf(path);
  const image = IMAGE[ext];
  if (image !== undefined) {
    return { kind: "image", mime: image };
  }
  const video = VIDEO[ext];
  return video !== undefined ? { kind: "video", mime: video } : null;
}
