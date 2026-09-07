export function intersectsViewport(element: Element, viewport: Element): boolean {
  const bounds = element.getBoundingClientRect();
  const visible = viewport.getBoundingClientRect();
  return bounds.bottom > visible.top && bounds.top < visible.bottom;
}

export function newestVisibleAgentElement<T extends HTMLElement>(selector: string): T | undefined {
  const body = document.querySelector<HTMLElement>(".agent-surface.active .agent-body");
  if (body === null) return undefined;
  const elements = body.querySelectorAll<T>(selector);
  for (let index = elements.length - 1; index >= 0; index -= 1) {
    const element = elements.item(index);
    if (intersectsViewport(element, body)) return element;
  }
  return undefined;
}
