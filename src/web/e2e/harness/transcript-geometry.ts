/** Reads the owned viewport's rendered geometry, without exposing its private scroll model. */
export function transcriptGeometry(element: HTMLElement): {
  offset: number;
  contentHeight: number;
  height: number;
  bottomDistance: number;
} {
  const rows = element.querySelector<HTMLElement>(".monaco-list-rows");
  const viewport = element.querySelector<HTMLElement>(".monaco-list");
  if (rows === null || viewport === null)
    throw new Error("Transcript list geometry is unavailable");
  const offset = 0 - Number.parseFloat(rows.style.top);
  const contentHeight = Number.parseFloat(rows.style.height);
  const height = viewport.clientHeight;
  if (![offset, contentHeight, height].every(Number.isFinite)) {
    throw new Error("Transcript list has invalid rendered geometry");
  }
  return { offset, contentHeight, height, bottomDistance: contentHeight - height - offset };
}
