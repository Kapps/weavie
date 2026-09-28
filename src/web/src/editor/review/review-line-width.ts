const owners = new WeakSet<object>();

/** View zones cannot reserve width when Monaco wraps to the viewport. */
export function reserveReviewLineWidth(
  layout: { readonly _maxLineWidth: number; setMaxLineWidth(width: number): void },
  preparedWidth: number,
) {
  if (owners.has(layout)) throw new Error("The review line width already has an owner");
  const descriptor = Object.getOwnPropertyDescriptor(layout, "setMaxLineWidth");
  const original = layout.setMaxLineWidth;
  let measured = layout._maxLineWidth;
  let reserved = true;
  let disposed = false;
  const publish = function (this: typeof layout, width: number): void {
    measured = width;
    original.call(this, reserved ? Math.max(width, preparedWidth) : width);
  };
  owners.add(layout);
  layout.setMaxLineWidth = publish;
  try {
    publish.call(layout, measured);
  } catch (error) {
    owners.delete(layout);
    if (descriptor) Object.defineProperty(layout, "setMaxLineWidth", descriptor);
    else Reflect.deleteProperty(layout, "setMaxLineWidth");
    throw error;
  }
  return {
    clear: (): void => {
      if (!reserved || disposed) return;
      if (layout.setMaxLineWidth !== publish)
        throw new Error("The review line-width reservation was replaced");
      reserved = false;
      original.call(layout, measured);
    },
    dispose: (): void => {
      if (disposed) return;
      disposed = true;
      reserved = false;
      owners.delete(layout);
      if (layout.setMaxLineWidth !== publish)
        throw new Error("The review line-width reservation was replaced");
      if (descriptor) Object.defineProperty(layout, "setMaxLineWidth", descriptor);
      else Reflect.deleteProperty(layout, "setMaxLineWidth");
      original.call(layout, measured);
    },
  };
}
