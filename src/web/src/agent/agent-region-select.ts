import { notify } from "../notify/notify";
import { cropAgentImage } from "./agent-images";
import "./agent-region-select.css";

// The smallest drag, in CSS px, that counts as a box rather than a click.
const minimumBox = 8;

/**
 * Lets the user box a region of an image or diagram inside `host`; the region is cropped to a PNG at the
 * picture's own resolution and handed to `attach`.
 */
export function installRegionSelect(host: HTMLElement, attach: (png: Blob) => void): () => void {
  const onPointerDown = (event: PointerEvent): void => {
    const target = (event.target as Element | null)?.closest<HTMLImageElement | SVGSVGElement>(
      "span.agent-image img, .agent-mermaid-block svg",
    );
    if (event.button !== 0 || target === null || target === undefined) return;
    event.preventDefault();
    const bounds = target.getBoundingClientRect();
    const box = document.createElement("div");
    box.className = "agent-region-box";
    document.body.append(box);
    const start = { x: event.clientX, y: event.clientY };
    const rect = (x: number, y: number): DOMRect => {
      const left = Math.max(bounds.left, Math.min(start.x, x));
      const top = Math.max(bounds.top, Math.min(start.y, y));
      const right = Math.min(bounds.right, Math.max(start.x, x));
      const bottom = Math.min(bounds.bottom, Math.max(start.y, y));
      return new DOMRect(left, top, Math.max(0, right - left), Math.max(0, bottom - top));
    };
    const draw = (move: PointerEvent): void => {
      const area = rect(move.clientX, move.clientY);
      Object.assign(box.style, {
        left: `${area.left}px`,
        top: `${area.top}px`,
        width: `${area.width}px`,
        height: `${area.height}px`,
      });
    };
    const finish = (up: PointerEvent): void => {
      window.removeEventListener("pointermove", draw);
      window.removeEventListener("pointerup", finish);
      box.remove();
      const area = rect(up.clientX, up.clientY);
      if (area.width < minimumBox || area.height < minimumBox) return;
      const png =
        target instanceof HTMLImageElement
          ? cropAgentImage(target, {
              x: area.left - bounds.left,
              y: area.top - bounds.top,
              width: area.width,
              height: area.height,
            })
          : cropDiagram(target, bounds, area);
      void png
        .then(attach)
        .catch((error: unknown) => notify("error", `Couldn't attach the region: ${String(error)}`));
    };
    window.addEventListener("pointermove", draw);
    window.addEventListener("pointerup", finish);
  };
  host.addEventListener("pointerdown", onPointerDown);
  return () => host.removeEventListener("pointerdown", onPointerDown);
}

async function cropDiagram(svg: SVGSVGElement, bounds: DOMRect, area: DOMRect): Promise<Blob> {
  const image = await rasterize(svg, bounds);
  const scale = image.naturalWidth / bounds.width;
  const canvas = document.createElement("canvas");
  canvas.width = Math.round(area.width * scale);
  canvas.height = Math.round(area.height * scale);
  canvas
    .getContext("2d")!
    .drawImage(
      image,
      (area.left - bounds.left) * scale,
      (area.top - bounds.top) * scale,
      canvas.width,
      canvas.height,
      0,
      0,
      canvas.width,
      canvas.height,
    );
  return new Promise((resolve, reject) =>
    canvas.toBlob(
      (png) =>
        png === null ? reject(new Error("The region could not be cropped.")) : resolve(png),
      "image/png",
    ),
  );
}

// A diagram is SVG drawn by the page; it becomes an image at twice its on-screen size so the crop stays legible.
async function rasterize(svg: SVGSVGElement, bounds: DOMRect): Promise<HTMLImageElement> {
  const copy = svg.cloneNode(true) as SVGSVGElement;
  copy.setAttribute("width", String(bounds.width * 2));
  copy.setAttribute("height", String(bounds.height * 2));
  copy.style.fontFamily = getComputedStyle(svg).fontFamily;
  copy.style.color = getComputedStyle(svg).color;
  const url = URL.createObjectURL(
    new Blob([new XMLSerializer().serializeToString(copy)], { type: "image/svg+xml" }),
  );
  try {
    const image = new Image();
    image.src = url;
    await image.decode();
    return image;
  } finally {
    URL.revokeObjectURL(url);
  }
}
