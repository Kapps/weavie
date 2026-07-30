const MIRRORED_STYLES = [
  "direction",
  "font",
  "font-feature-settings",
  "font-kerning",
  "font-variation-settings",
  "letter-spacing",
  "line-height",
  "overflow-wrap",
  "padding",
  "tab-size",
  "text-align",
  "text-indent",
  "text-transform",
  "white-space",
  "word-break",
  "word-spacing",
] as const;

/** Whether the caret shares the textarea's first rendered line, including soft-wrapped text. */
export function caretOnFirstVisualLine(element: HTMLTextAreaElement): boolean {
  return caretSharesVisualLine(element, element.selectionStart, 0);
}

/** Whether the caret shares the textarea's last rendered line, including soft-wrapped text. */
export function caretOnLastVisualLine(element: HTMLTextAreaElement): boolean {
  return caretSharesVisualLine(element, element.selectionEnd, element.value.length);
}

function caretSharesVisualLine(
  element: HTMLTextAreaElement,
  caret: number,
  boundary: number,
): boolean {
  const document = element.ownerDocument;
  const computed = document.defaultView?.getComputedStyle(element);
  if (computed === undefined) {
    return false;
  }

  const mirror = document.createElement("div");
  mirror.style.position = "fixed";
  mirror.style.visibility = "hidden";
  mirror.style.pointerEvents = "none";
  mirror.style.boxSizing = "border-box";
  mirror.style.width = `${element.clientWidth}px`;
  mirror.style.border = "0";
  for (const property of MIRRORED_STYLES) {
    mirror.style.setProperty(property, computed.getPropertyValue(property));
  }

  const text = document.createTextNode(element.value);
  const range = document.createRange();
  mirror.append(text);
  document.body.append(mirror);
  try {
    const caretTop = collapsedCaretTop(range, text, caret);
    const boundaryTop = collapsedCaretTop(range, text, boundary);
    return (
      (caretTop ?? blankCaretTop(mirror, range, element.value, caret)) ===
      (boundaryTop ?? blankCaretTop(mirror, range, element.value, boundary))
    );
  } finally {
    mirror.remove();
  }
}

function collapsedCaretTop(range: Range, text: Text, offset: number): number | null {
  range.setStart(text, offset);
  range.collapse(true);
  return range.getClientRects().item(0)?.top ?? null;
}

function blankCaretTop(
  mirror: HTMLDivElement,
  range: Range,
  value: string,
  offset: number,
): number {
  const text = mirror.ownerDocument.createTextNode(
    `${value.slice(0, offset)}\u200b${value.slice(offset)}`,
  );
  mirror.replaceChildren(text);
  range.setStart(text, offset);
  range.setEnd(text, offset + 1);
  return range.getBoundingClientRect().top;
}
