import { createSignal } from "solid-js";
import { hostConnection, LOCAL_BACKEND_ID, registerHostFeature } from "../bridge";

// App-global session-rail UI state, persisted host-side in ~/.weavie/rail-state.json (not localStorage): the
// backend a session was last created on and the session panel's layout. Setters update the local signal
// optimistically and tell the host, which echoes the canonical state back. Registered at module load, before
// main.tsx sends `ready`.

const [lastLocationSig, setLastLocationSig] = createSignal("local");
const [panelPinnedSig, setPanelPinnedSig] = createSignal(false);
const [panelWidthSig, setPanelWidthSig] = createSignal(300);
const [parkedCollapsedSig, setParkedCollapsedSig] = createSignal(true);

interface RailState {
  lastLocation: string;
  panelPinned: boolean;
  panelWidth: number;
  parkedCollapsed: boolean;
}

function applyRailState(state: RailState): void {
  setLastLocationSig(state.lastLocation);
  setPanelPinnedSig(state.panelPinned);
  setPanelWidthSig(state.panelWidth);
  setParkedCollapsedSig(state.parkedCollapsed);
}

registerHostFeature((connection) => {
  if (!connection.isLocal) {
    return () => {};
  }
  const offHello = connection.onHello((hello) => applyRailState(hello.rail));
  const offState = connection.host.feature("rail").on<RailState>("changed", applyRailState);
  return () => {
    offHello();
    offState();
  };
});

const publish = (name: string, payload: unknown): void => {
  hostConnection(LOCAL_BACKEND_ID)?.host.feature("rail").publish(name, payload);
};

/** The backend id the last session was created on (defaults to "local"). The caller validates it still exists. */
export const lastLocation = lastLocationSig;

/** Remember the backend a session was just created on (or an agent just added), for the next prompt. */
export function setLastLocation(backendId: string): void {
  setLastLocationSig(backendId);
  publish("setLastLocation", { location: backendId });
}

/** Whether the session panel is docked open in the rail's place (reactive). */
export const panelPinned = panelPinnedSig;

/** Dock or undock the session panel. */
export function setPanelPinned(pinned: boolean): void {
  setPanelPinnedSig(pinned);
  publish("setPanelPinned", { pinned });
}

/** The pinned session panel's width in CSS pixels (reactive). */
export const panelWidth = panelWidthSig;

/** Remember the pinned session panel's resized width. */
export function setPanelWidth(width: number): void {
  setPanelWidthSig(width);
  publish("setPanelWidth", { width });
}

/** Whether unloaded ("parked") sessions are folded away in the rail and the session panel (reactive). */
export const parkedCollapsed = parkedCollapsedSig;

/** Fold or unfold parked sessions. */
export function setParkedCollapsed(collapsed: boolean): void {
  setParkedCollapsedSig(collapsed);
  publish("setParkedCollapsed", { collapsed });
}
