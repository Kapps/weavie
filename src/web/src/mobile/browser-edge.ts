// The browser navigates history from both screen edges — iOS pops back off the left one and forward off
// the right — and Weavie's surfaces are history entries, so a swipe there already is the navigation.
export const BROWSER_EDGE_WIDTH = 32;

/** Whether a touch begins in a strip the browser navigates history from, so the browser owns it alone. */
export function startsOnBrowserEdge(clientX: number): boolean {
  return clientX <= BROWSER_EDGE_WIDTH || clientX >= window.innerWidth - BROWSER_EDGE_WIDTH;
}
