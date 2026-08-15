import { createMemo, createSignal } from "solid-js";
import { activeBackendId, LOCAL_BACKEND_ID, registerHostFeature } from "../bridge";
import { notify } from "../notify/notify";

export type UpdateHold = {
  session: string;
  reason: "working" | "needs-input" | "shell-job" | "waiting-on-task" | "recent-input";
};

interface UpdateState {
  holds: UpdateHold[] | null;
  restarting: boolean;
  pending: boolean;
}

const UPDATED_KEY = "weavie-updated-to";
const UPDATE_TOAST_KEY = "weavie-update-ready";
const EMPTY: UpdateState = { holds: null, restarting: false, pending: false };
const [states, setStates] = createSignal(new Map<string, UpdateState>());
const [builds, setBuilds] = createSignal(new Map<string, string>());

function notifyUpdated(buildNumber: string): void {
  notify("info", `Weavie updated to build ${buildNumber}.`);
}

function updateState(backendId: string, update: (state: UpdateState) => UpdateState): void {
  setStates((previous) => {
    const next = new Map(previous);
    next.set(backendId, update(next.get(backendId) ?? EMPTY));
    return next;
  });
}

registerHostFeature((connection) => {
  let buildNumber: string | null = null;
  const feature = connection.host.feature("updates");
  const offPending = feature.on<{ holds: UpdateHold[] }>("pending", ({ holds }) => {
    updateState(connection.id, (state) => {
      if (!state.pending) {
        notify(
          "info",
          "Update ready — it'll apply once your workspace is quiet.",
          `${UPDATE_TOAST_KEY}:${connection.id}`,
        );
      }
      return { holds, restarting: false, pending: true };
    });
  });
  const offRestarting = feature.on("restarting", () => {
    updateState(connection.id, (state) => ({ ...state, restarting: true }));
  });
  const offHello = connection.onHello((hello) => {
    const previousBuildNumber = buildNumber;
    buildNumber = hello.buildNumber;
    setBuilds((previous) => new Map(previous).set(connection.id, hello.buildNumber));
    if (connection.isLocal) {
      const boot = window.__WEAVIE_SHELL__?.buildNumber;
      if (boot !== undefined && boot !== "" && hello.buildNumber !== boot) {
        window.sessionStorage.setItem(UPDATED_KEY, hello.buildNumber);
        window.location.reload();
        return;
      }
    }
    updateState(connection.id, (state) => {
      if (previousBuildNumber !== null && hello.buildNumber !== previousBuildNumber) {
        if (!connection.isLocal && (state.pending || state.restarting)) {
          notifyUpdated(hello.buildNumber);
        }
        return EMPTY;
      }
      if (state.restarting) {
        notify(
          "warn",
          "The update didn't apply — the worker is back on the same build (it may have been rolled back). Check the runner page.",
        );
        return EMPTY;
      }
      return { ...state, holds: null, restarting: false };
    });
  });
  return () => {
    offPending();
    offRestarting();
    offHello();
    setStates((previous) => {
      const next = new Map(previous);
      next.delete(connection.id);
      return next;
    });
    setBuilds((previous) => {
      const next = new Map(previous);
      next.delete(connection.id);
      return next;
    });
  };
});

/**
 * The builds a remote backend and this client run when they differ. Weavie ships its host and client
 * together, so a backend on another build speaks a different protocol: its features go missing rather than
 * degrade, and the user has to be told which side to update.
 */
export function backendBuildMismatch(
  backendId: string,
): { client: string; backend: string } | null {
  const known = builds();
  const client = known.get(LOCAL_BACKEND_ID);
  const backend = known.get(backendId);
  return backendId !== LOCAL_BACKEND_ID &&
    client !== undefined &&
    backend !== undefined &&
    client !== backend
    ? { client, backend }
    : null;
}

export function surfacePostUpdateNotice(): void {
  const updatedTo = window.sessionStorage.getItem(UPDATED_KEY);
  if (updatedTo !== null) {
    window.sessionStorage.removeItem(UPDATED_KEY);
    notifyUpdated(updatedTo);
  }
}

const selectedState = (): UpdateState => states().get(activeBackendId()) ?? EMPTY;

export const updateHolds = createMemo(() => selectedState().holds);
export const updatePending = createMemo(() => selectedState().pending);
export const updateRestarting = createMemo(() => selectedState().restarting);

/** The selected backend's build mismatch, or null while it matches this client. */
export const activeBackendBuildMismatch = createMemo(() => backendBuildMismatch(activeBackendId()));
