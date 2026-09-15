import { afterEach, expect, it, vi } from "vitest";

const { register, unregister, options } = vi.hoisted(() => ({
  register: vi.fn(),
  unregister: vi.fn(),
  options: { smoothScrolling: true },
}));
vi.mock("../chrome/middle-click-autoscroll", () => ({ registerScrollTarget: register }));
vi.mock("../editor-options", () => ({ currentEditorOptions: () => options }));

import { installTranscriptInput } from "./AgentTranscriptInput";

function setup() {
  register.mockReturnValue(unregister);
  vi.stubGlobal("getComputedStyle", () => ({ lineHeight: "20px" }));
  const body = {
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  } as unknown as HTMLElement;
  const keyboardElement = {} as HTMLElement;
  const target = {
    height: () => 500,
    contentHeight: () => 4000,
    scrollBy: vi.fn(),
    jumpTo: vi.fn(),
  };
  const dispose = installTranscriptInput(body, target, keyboardElement);
  const listener = vi.mocked(body.addEventListener).mock.calls[0]![1] as (
    event: KeyboardEvent,
  ) => void;
  const press = (key: string, properties: Partial<KeyboardEvent>) => {
    const event = { key, target: body, preventDefault: vi.fn(), ...properties };
    listener(event as KeyboardEvent);
    return event;
  };
  return { body, keyboardElement, target, dispose, press };
}
afterEach(() => {
  vi.clearAllMocks();
  vi.unstubAllGlobals();
  options.smoothScrolling = true;
});
it("uses model geometry from either viewport focus node and live motion settings", () => {
  const { press, target, keyboardElement } = setup();
  expect(press("ArrowDown", {}).preventDefault).toHaveBeenCalled();
  expect(target.scrollBy).toHaveBeenLastCalledWith(20, true);
  press("PageUp", { target: keyboardElement });
  expect(target.scrollBy).toHaveBeenLastCalledWith(-500, true);
  options.smoothScrolling = false;
  press(" ", { shiftKey: true });
  expect(target.scrollBy).toHaveBeenLastCalledWith(-500, false);
  press("Home", {});
  expect(target.jumpTo).toHaveBeenLastCalledWith(0);
  press("End", {});
  expect(target.jumpTo).toHaveBeenLastCalledWith(4000);
});
it("preserves nested controls, modifiers, composition and consumed events", () => {
  const { press, target } = setup();
  for (const properties of [
    { target: {} as EventTarget },
    { defaultPrevented: true },
    { ctrlKey: true },
    { metaKey: true },
    { altKey: true },
    { shiftKey: true },
    { isComposing: true },
  ]) {
    expect(press("ArrowDown", properties).preventDefault).not.toHaveBeenCalled();
  }
  expect(target.scrollBy).not.toHaveBeenCalled();
  expect(target.jumpTo).not.toHaveBeenCalled();
});
it("registers model autoscroll and releases both input owners", () => {
  const { body, target, dispose } = setup();
  const registered = register.mock.calls[0]![1];
  expect(registered.x).toBeNull();
  expect(registered.y.canScroll()).toBe(true);
  registered.y.scrollBy(0.25);
  expect(target.scrollBy).toHaveBeenCalledWith(0.25, false);
  dispose();
  expect(unregister).toHaveBeenCalledOnce();
  expect(body.removeEventListener).toHaveBeenCalledWith(
    "keydown",
    vi.mocked(body.addEventListener).mock.calls[0]![1],
  );
});
