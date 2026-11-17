/** A window button's name, as its tooltip and accessible label. */
export function windowControlLabel(action: WindowControlAction, maximized: boolean): string {
  if (action === "minimize") return "Minimize";
  if (action === "close") return "Close";
  return maximized ? "Restore" : "Maximize";
}
