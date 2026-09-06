import { expect, it, vi } from "vitest";
import { evaluateWhen, onContextChanged } from "../commands/context";
import {
  closeFloatingPanel,
  dismissFloatingPopovers,
  registerFloatingPanel,
} from "./floating-panels";

it("raising a tool does not transiently remove floating availability or dismiss underlying surfaces", () => {
  const changed = vi.fn();
  const off = onContextChanged(changed);
  const closeFiles = vi.fn();
  const files = registerFloatingPanel("files", closeFiles, "tool");
  changed.mockClear();
  files.raise();
  expect(changed).not.toHaveBeenCalled();
  const closeSearch = vi.fn();
  const search = registerFloatingPanel("search", closeSearch, "tool");
  changed.mockClear();
  files.raise();
  files.raise();
  expect(changed).not.toHaveBeenCalled();
  expect(closeFloatingPanel()).toBe(true);
  expect(closeFiles).toHaveBeenCalledOnce();
  expect(closeSearch).not.toHaveBeenCalled();
  files.dispose();
  search.dispose();
  expect(evaluateWhen("floatingPanelOpen")).toBe(false);
  expect(closeFloatingPanel()).toBe(false);
  off();
});

it("tool activation retires transient popovers without closing tools", () => {
  const closeFiles = vi.fn();
  const files = registerFloatingPanel("files", closeFiles, "tool");
  const menu = registerFloatingPanel("context-menu", () => menu.dispose(), "popover");
  dismissFloatingPopovers();
  expect(closeFiles).not.toHaveBeenCalled();
  closeFloatingPanel();
  expect(closeFiles).toHaveBeenCalledOnce();
  files.dispose();
});
