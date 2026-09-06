import { createSignal } from "solid-js";
import { setContext } from "../commands/context";

interface FloatingPanel {
  id: string;
  role: "tool" | "popover";
  close: () => void;
}
const panels: FloatingPanel[] = [];
const [version, setVersion] = createSignal(0);

export function floatingPanelLevel(id: string): number {
  version();
  return 41 + panels.findIndex((panel) => panel.id === id);
}

/** Registers an open floating panel; the most recently raised panel owns dismissal. */
export function registerFloatingPanel(
  id: string,
  close: () => void,
  role: FloatingPanel["role"],
): {
  raise: () => void;
  dispose: () => void;
} {
  const panel = { id, close, role };
  const detach = (): void => {
    const index = panels.indexOf(panel);
    if (index >= 0) panels.splice(index, 1);
  };
  const publish = (): void => {
    setContext("floatingPanelOpen", panels.length > 0);
    setVersion((value) => value + 1);
  };
  const raise = (): void => {
    if (panels.at(-1) === panel) return;
    detach();
    panels.push(panel);
    publish();
  };
  raise();
  return {
    raise,
    dispose: () => {
      detach();
      publish();
    },
  };
}

/** Opening a tool retires transient popovers, whose visual layer sits above tool windows. */
export function dismissFloatingPopovers(): void {
  for (const panel of [...panels].reverse()) {
    if (panel.role === "popover") panel.close();
  }
}

/** Dismisses exactly one floating panel. Nested controls consume their event before this runs. */
export function closeFloatingPanel(): boolean {
  const panel = panels.at(-1);
  if (panel === undefined) return false;
  panel.close();
  return true;
}
