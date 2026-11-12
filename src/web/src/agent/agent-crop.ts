/** A rectangle in pixels. */
export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * Maps `region`, in a frame's CSS pixels, to the picture's own pixels, for a picture drawn with `object-fit: contain`
 * at `position` (each axis 0 to 1, as `object-position` percentages). Null when the region misses the picture.
 */
export function sourceRect(
  frame: { width: number; height: number },
  natural: { width: number; height: number },
  position: { x: number; y: number },
  region: Rect,
): Rect | null {
  const scale = Math.min(frame.width / natural.width, frame.height / natural.height);
  const left = (frame.width - natural.width * scale) * position.x;
  const top = (frame.height - natural.height * scale) * position.y;
  const x = Math.max(0, (region.x - left) / scale);
  const y = Math.max(0, (region.y - top) / scale);
  const width = Math.min(natural.width, (region.x + region.width - left) / scale) - x;
  const height = Math.min(natural.height, (region.y + region.height - top) / scale) - y;
  return width > 0 && height > 0 ? { x, y, width, height } : null;
}

/** Crops `area` of `image`, in its own pixels, to a PNG. */
export function cropPng(image: CanvasImageSource, area: Rect): Promise<Blob> {
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(area.width));
  canvas.height = Math.max(1, Math.round(area.height));
  canvas
    .getContext("2d")!
    .drawImage(image, area.x, area.y, area.width, area.height, 0, 0, canvas.width, canvas.height);
  return canvasBlob(canvas, "image/png", 1);
}

/** Encodes `canvas`, rejecting when the browser can't. */
export function canvasBlob(
  canvas: HTMLCanvasElement,
  type: string,
  quality: number,
): Promise<Blob> {
  return new Promise((resolve, reject) =>
    canvas.toBlob(
      (blob) => (blob === null ? reject(new Error("Couldn't encode the image.")) : resolve(blob)),
      type,
      quality,
    ),
  );
}
