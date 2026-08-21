import { createSignal } from "solid-js";
import type { CommandResult } from "./commands/types";
import { basename } from "./editor/fs-path";
import { ChunkedMessageReceiver } from "./messaging/chunked-message";
import { type ClientSession, HostConnection, type HostHello } from "./messaging/host-connection";
import { parseEnvelope } from "./messaging/message-envelope";
import { PAGE_EPOCH } from "./messaging/page-epoch";
import type { BackendEndpoint, BackendInfo, PullRequestInfo } from "./messaging/protocol-types";
import { SelectionSequencer } from "./messaging/selection-sequencer";
import { clearNotification, notify } from "./notify/notify";

export { ClientSession, HostConnection } from "./messaging/host-connection";
export type * from "./messaging/protocol-types";

export const LOCAL_BACKEND_ID = "local";
const VIEW_ATTACHMENT = { pageEpoch: PAGE_EPOCH };

type SessionFeatureInstaller = (session: ClientSession) => undefined | (() => void);
type ViewFeatureInstaller = (session: ClientSession) => undefined | (() => void);
type HostFeatureInstaller = (connection: HostConnection) => undefined | (() => void);
type SelectionListener = (session: ClientSession | null) => void;
type BackendDisconnectedHandler = (backendId: string) => void;

interface BridgeTransport {
  send(json: string): void;
  dispose(): void;
}

interface Backend {
  info: BackendInfo;
  connection: HostConnection;
  transport: BridgeTransport;
}

const backends = new Map<string, Backend>();
const liveSessions = new Set<ClientSession>();
const sessionInstallers = new Set<SessionFeatureInstaller>();
const hostInstallers = new Set<HostFeatureInstaller>();
const selectionListeners = new Set<SelectionListener>();
const disconnectedListeners = new Set<BackendDisconnectedHandler>();
const installedSessionFeatures = new Map<
  ClientSession,
  Map<SessionFeatureInstaller, (() => void) | undefined>
>();
const installedHostFeatures = new Map<
  HostConnection,
  Map<HostFeatureInstaller, (() => void) | undefined>
>();
const [backendList, setBackendList] = createSignal<BackendInfo[]>([]);
const [selected, setSelected] = createSignal<ClientSession | null>(null);
const [resourceBases, setResourceBases] = createSignal<Record<string, string>>({});
type SelectionLocation = NonNullable<HostHello["rail"]["selected"]>;
let preferredSelection: SelectionLocation | null = null;
let localSelectionReady = false;
let selectionChosenBeforeRestore = false;

export const connectedBackends = backendList;
export const selectedSession = selected;
export const activeBackendId = (): string => selected()?.connection.id ?? LOCAL_BACKEND_ID;

export function clientSession(backendId: string, slot: string): ClientSession | undefined {
  return hostConnection(backendId)?.sessionForSlot(slot);
}

export function clientSessionAt(
  backendId: string,
  address: { slot: string; incarnation: string },
): ClientSession | undefined {
  return hostConnection(backendId)?.session(address);
}

export function sessionForSlot(backendId: string, slot: string): ClientSession | undefined {
  return clientSession(backendId, slot);
}

export function hostConnection(backendId: string): HostConnection | undefined {
  return backends.get(backendId)?.connection;
}

const clientSelections = new SelectionSequencer<ClientSession>((session) => {
  if (!liveSessions.has(session)) {
    return false;
  }
  rememberSelection(session);
  commitSelection(session);
  return true;
});

export function selectClientSession(session: ClientSession): void {
  if (!liveSessions.has(session)) {
    return;
  }
  beginClientSelection()(session);
}

/** Returns a commit that succeeds only while this remains the page's newest selection intent. */
export function beginClientSelection(): (session: ClientSession) => boolean {
  return clientSelections.beginIntent();
}

/** Reserves invocation order without superseding a selection unless its result requests activation. */
export function beginClientSelectionCandidate(): (session: ClientSession) => boolean {
  return clientSelections.beginCandidate();
}

export function waitForClientSession(
  backendId: string,
  address: { slot: string; incarnation: string },
): Promise<ClientSession> {
  const connection = hostConnection(backendId);
  if (connection === undefined) {
    return Promise.reject(new Error(`${backendId} is not connected.`));
  }
  const current = connection.session(address);
  if (current !== undefined) {
    return Promise.resolve(current);
  }
  return new Promise((resolve) => {
    let cleanup: (() => void) | undefined;
    let settled = false;
    const listener = (): void => {
      const session = connection.session(address);
      if (session === undefined) {
        return;
      }
      settled = true;
      cleanup?.();
      resolve(session);
    };
    cleanup = connection.onCatalog(listener);
    if (settled) {
      cleanup();
    }
  });
}

export function onSelectedSession(listener: SelectionListener): () => void {
  selectionListeners.add(listener);
  const session = selected();
  runSafely(session?.connection.id ?? LOCAL_BACKEND_ID, () => listener(session));
  return () => selectionListeners.delete(listener);
}

export function registerSessionFeature(installer: SessionFeatureInstaller): () => void {
  sessionInstallers.add(installer);
  for (const session of liveSessions) {
    installSessionFeature(session, installer);
  }
  return () => {
    sessionInstallers.delete(installer);
    for (const [session, installed] of installedSessionFeatures) {
      runSafely(session.connection.id, () => installed.get(installer)?.());
      installed.delete(installer);
    }
  };
}

export function registerViewFeature(installer: ViewFeatureInstaller): () => void {
  let cleanup = (): void => {};
  const stop = onSelectedSession((session) => {
    const previousCleanup = cleanup;
    cleanup = (): void => {};
    runSafely(session?.connection.id ?? LOCAL_BACKEND_ID, previousCleanup);
    if (session !== null) {
      runSafely(session.connection.id, () => {
        cleanup = installer(session) ?? (() => {});
      });
    }
  });
  return () => {
    stop();
    const finalCleanup = cleanup;
    cleanup = (): void => {};
    runSafely(selected()?.connection.id ?? LOCAL_BACKEND_ID, finalCleanup);
  };
}

export function registerHostFeature(installer: HostFeatureInstaller): () => void {
  hostInstallers.add(installer);
  for (const backend of backends.values()) {
    installHostFeature(backend.connection, installer);
  }
  return () => {
    hostInstallers.delete(installer);
    for (const [connection, installed] of installedHostFeatures) {
      runSafely(connection.id, () => installed.get(installer)?.());
      installed.delete(installer);
    }
  };
}

function installSessionFeature(session: ClientSession, installer: SessionFeatureInstaller): void {
  let installed = installedSessionFeatures.get(session);
  if (installed === undefined) {
    installed = new Map();
    installedSessionFeatures.set(session, installed);
  }
  if (!installed.has(installer)) {
    try {
      installed.set(installer, installer(session) ?? undefined);
    } catch (error) {
      installed.set(installer, undefined);
      session.connection.reportError(error);
    }
  }
}

function installHostFeature(connection: HostConnection, installer: HostFeatureInstaller): void {
  let installed = installedHostFeatures.get(connection);
  if (installed === undefined) {
    installed = new Map();
    installedHostFeatures.set(connection, installed);
  }
  if (!installed.has(installer)) {
    try {
      installed.set(installer, installer(connection) ?? undefined);
    } catch (error) {
      installed.set(installer, undefined);
      connection.reportError(error);
    }
  }
}

function removeHostFeatures(connection: HostConnection): void {
  for (const cleanup of installedHostFeatures.get(connection)?.values() ?? []) {
    runSafely(connection.id, () => cleanup?.());
  }
  installedHostFeatures.delete(connection);
}

function defaultSession(): ClientSession | null {
  return hostConnection(LOCAL_BACKEND_ID)?.sessions[0] ?? null;
}

function selectionLocation(session: ClientSession): SelectionLocation {
  return { backendId: session.connection.id, slot: session.address.slot };
}

function sameSelection(left: SelectionLocation | null, right: SelectionLocation | null): boolean {
  return (
    left === right ||
    (left !== null &&
      right !== null &&
      left.backendId === right.backendId &&
      left.slot === right.slot)
  );
}

function publishSelection(location: SelectionLocation): void {
  runSafely(LOCAL_BACKEND_ID, () =>
    hostConnection(LOCAL_BACKEND_ID)?.host.feature("rail").publish("setSelected", location),
  );
}

function rememberSelection(session: ClientSession): void {
  const location = selectionLocation(session);
  if (sameSelection(preferredSelection, location)) {
    return;
  }
  preferredSelection = location;
  if (localSelectionReady) {
    publishSelection(location);
  } else {
    selectionChosenBeforeRestore = true;
  }
}

function restoreSelection(hello: HostHello): void {
  if (!selectionChosenBeforeRestore) {
    preferredSelection = hello.rail.selected;
  }
  localSelectionReady = true;
  reconcileSessions();
  if (selectionChosenBeforeRestore && preferredSelection !== null) {
    publishSelection(preferredSelection);
  }
  selectionChosenBeforeRestore = false;
}

function preferredSession(): ClientSession | undefined {
  if (preferredSelection === null) {
    return undefined;
  }
  return hostConnection(preferredSelection.backendId)?.sessionForSlot(preferredSelection.slot);
}

function preferredSelectionIsMissing(): boolean {
  if (preferredSelection === null) {
    return false;
  }
  const connection = hostConnection(preferredSelection.backendId);
  return (
    connection?.currentHello !== null &&
    connection?.currentHello !== undefined &&
    !connection.currentCatalog.some((entry) => entry.id === preferredSelection?.slot)
  );
}

function reconcileSessions(): void {
  const current = new Set([...backends.values()].flatMap((backend) => backend.connection.sessions));
  for (const session of liveSessions) {
    if (current.has(session)) {
      continue;
    }
    for (const cleanup of installedSessionFeatures.get(session)?.values() ?? []) {
      runSafely(session.connection.id, () => cleanup?.());
    }
    installedSessionFeatures.delete(session);
    liveSessions.delete(session);
  }
  for (const session of current) {
    if (liveSessions.has(session)) {
      continue;
    }
    liveSessions.add(session);
    for (const installer of sessionInstallers) {
      installSessionFeature(session, installer);
    }
  }

  const active = selected();
  const preferred = preferredSession();
  if (preferred !== undefined && current.has(preferred)) {
    commitSelection(preferred);
    return;
  }
  if (preferredSelectionIsMissing()) {
    const fallback = active !== null && current.has(active) ? active : defaultSession();
    if (fallback !== null) {
      rememberSelection(fallback);
    }
    commitSelection(fallback);
    return;
  }
  if (active === null || !current.has(active)) {
    commitSelection(defaultSession());
  }
}

function commitSelection(session: ClientSession | null): void {
  const previous = selected();
  if (previous === session) {
    return;
  }
  setSelected(session);
  for (const listener of selectionListeners) {
    runSafely(session?.connection.id ?? previous?.connection.id ?? LOCAL_BACKEND_ID, () =>
      listener(session),
    );
  }
  if (
    previous !== null &&
    previous.connection !== session?.connection &&
    liveSessions.has(previous)
  ) {
    runSafely(previous.connection.id, () =>
      previous.feature("view").publish("detach", VIEW_ATTACHMENT),
    );
  }
  if (session !== null) {
    runSafely(session.connection.id, () =>
      session.feature("view").publish("attach", VIEW_ATTACHMENT),
    );
  }
}

export type BackendPhase = "connecting" | "online" | "reconnecting";

const [phases, setPhases] = createSignal<Map<string, BackendPhase>>(new Map());
const phaseListeners = new Set<(backendId: string, phase: BackendPhase) => void>();

export const backendPhase = (backendId: string): BackendPhase =>
  phases().get(backendId) ?? "online";
export const activeBackendPhase = (): BackendPhase => backendPhase(activeBackendId());
export const activeBackendOffline = (): boolean => activeBackendPhase() !== "online";

export function onBackendPhase(
  listener: (backendId: string, phase: BackendPhase) => void,
): () => void {
  phaseListeners.add(listener);
  return () => phaseListeners.delete(listener);
}

export function onBackendDisconnected(listener: BackendDisconnectedHandler): () => void {
  disconnectedListeners.add(listener);
  return () => disconnectedListeners.delete(listener);
}

function setBackendPhase(backendId: string, phase: BackendPhase): void {
  if (backendPhase(backendId) === phase) {
    return;
  }
  setPhases((previous) => new Map(previous).set(backendId, phase));
  for (const listener of phaseListeners) {
    runSafely(backendId, () => listener(backendId, phase));
  }
}

function clearBackendPhase(backendId: string): void {
  setPhases((previous) => {
    const next = new Map(previous);
    next.delete(backendId);
    return next;
  });
}

function createConnection(
  id: string,
  name: string,
  isLocal: boolean,
  send: (json: string) => void,
): HostConnection {
  const connection = new HostConnection(id, name, isLocal, send, (error) => reportError(id, error));
  connection.onCatalog(reconcileSessions);
  connection.onHello((hello) => {
    if (connection.isLocal) {
      restoreSelection(hello);
    }
    const active = selected();
    if (active?.connection === connection) {
      active.feature("view").publish("attach", VIEW_ATTACHMENT);
    }
  });
  return connection;
}

function addBackend(info: BackendInfo, transport: BridgeTransport): Backend {
  const connection = createConnection(info.id, info.name, info.isLocal, (json) =>
    transport.send(json),
  );
  const backend = { info, transport, connection };
  backends.set(info.id, backend);
  for (const installer of hostInstallers) {
    installHostFeature(connection, installer);
  }
  setBackendList([...backends.values()].map((candidate) => candidate.info));
  return backend;
}

function receiveRaw(backendId: string, raw: string): void {
  const envelope = parseEnvelope(raw);
  if (envelope === null) {
    reportError(backendId, new Error(`Malformed host message: ${raw.slice(0, 200)}`));
    return;
  }
  hostConnection(backendId)?.receive(envelope);
}

function selectedForBackend(backendId: string): ClientSession | undefined {
  const active = selected();
  return active?.connection.id === backendId ? active : undefined;
}

export async function invokeCommandOnBackend(
  backendId: string,
  id: string,
  args: unknown,
): Promise<CommandResult> {
  const source = selectedForBackend(backendId);
  if (source === undefined) {
    return { ok: false, error: "No live session is available." };
  }
  try {
    return await source.feature("commands").request<
      CommandResult,
      {
        id: string;
        args: unknown;
      }
    >("invoke", { id, args });
  } catch (error) {
    return {
      ok: false,
      error: error instanceof Error ? error.message : String(error),
    };
  }
}

export async function invokeSessionCommandOnBackend(
  backendId: string,
  id: string,
  args: unknown,
): Promise<CommandResult> {
  const connection = hostConnection(backendId);
  if (connection === undefined) {
    return { ok: false, error: "The host is not connected." };
  }
  try {
    return await connection.host
      .feature("sessions")
      .request<CommandResult, { id: string; args: unknown }>("invoke", { id, args });
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : String(error) };
  }
}

export async function invokeClientCommandOnHost(id: string, args: unknown): Promise<CommandResult> {
  const connection = hostConnection(LOCAL_BACKEND_ID);
  if (connection === undefined) {
    return { ok: false, error: "The local host is not connected." };
  }
  try {
    return await connection.host
      .feature("commands")
      .request<CommandResult, { id: string; args: unknown }>("invoke", { id, args });
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : String(error) };
  }
}

export function requestBranches(backendId: string): Promise<string[]> {
  const connection = hostConnection(backendId);
  return connection === undefined
    ? Promise.reject(new Error("The host is not connected."))
    : connection.host.feature("git").request("branches", {});
}

export interface BranchPreviewResult {
  branch: string;
  error: string | null;
}

export interface EncodedImageAttachment {
  id: string;
  mime: string;
  dataB64: string;
}

export function requestBranchPreview(
  backendId: string,
  prompt: string,
  attachments: readonly EncodedImageAttachment[],
  agentProviderId: string,
  signal: AbortSignal,
): Promise<BranchPreviewResult> {
  const connection = hostConnection(backendId);
  const active = selected();
  return connection === undefined
    ? Promise.reject(new Error("The host is not connected."))
    : connection.host.feature("sessionCreation").request(
        "previewBranch",
        {
          sourceId: active?.connection.id === backendId ? active.address.slot : null,
          prompt,
          attachments,
          agentProviderId,
        },
        signal,
      );
}

export function requestDiffRefs(backendId: string): Promise<string[]> {
  const session = selectedForBackend(backendId);
  return session === undefined
    ? Promise.reject(new Error("No live session is available."))
    : session.feature("files").request("refs", {});
}

export function requestPullRequests(backendId: string, query: string): Promise<PullRequestInfo[]> {
  const session = selectedForBackend(backendId);
  return session === undefined
    ? Promise.reject(new Error("No live session is available."))
    : session.feature("pullRequests").request("list", { query });
}

export function resolvePullRequest(
  backendId: string,
  target: { number: number; owner: string; repo: string },
): Promise<PullRequestInfo | null> {
  const session = selectedForBackend(backendId);
  return session === undefined
    ? Promise.reject(new Error("No live session is available."))
    : session.feature("pullRequests").request("resolve", target);
}

export function mediaResourceUrl(
  session: ClientSession,
  path: string,
  revision: number,
): string | null {
  const base = resourceBases()[session.connection.id];
  if (base === undefined) {
    return null;
  }
  const url = new URL(base, window.location.href);
  url.pathname = `${url.pathname.replace(/\/$/, "")}/${encodeURIComponent(basename(path))}`;
  url.searchParams.set("session", session.address.incarnation);
  url.searchParams.set("path", path);
  url.searchParams.set("rev", revision.toString());
  return url.toString();
}

export function backendName(backendId: string): string {
  return backends.get(backendId)?.info.name ?? backendId;
}

export function log(level: "info" | "warn" | "error", message: string): void {
  try {
    hostConnection(LOCAL_BACKEND_ID)
      ?.host.feature("diagnostics")
      .publish("log", { level, message });
  } catch (error) {
    console.error(message, error);
  }
}

let browserHostedShell = false;

export function isBrowserHostedShell(): boolean {
  return browserHostedShell;
}

class NativeTransport implements BridgeTransport {
  send(json: string): void {
    const webkit = window.webkit?.messageHandlers?.weavie;
    if (webkit !== undefined) {
      webkit.postMessage(json);
      return;
    }
    const webview = window.chrome?.webview;
    if (webview !== undefined) {
      webview.postMessage(json);
      return;
    }
    throw new Error("The native host bridge is unavailable.");
  }

  dispose(): void {}
}

const connectionNotificationKey = (backendId: string): string => `connection:${backendId}`;

class WebSocketTransport implements BridgeTransport {
  private socket: WebSocket | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private reconnectDelayMs = 500;
  private disposed = false;
  private opened = false;
  private readonly messages = new ChunkedMessageReceiver();

  constructor(
    private readonly backendId: string,
    private readonly label: string,
    private readonly resolveEndpoint: () => Promise<BackendEndpoint>,
  ) {
    setBackendPhase(backendId, "connecting");
  }

  start(): void {
    this.connect();
  }

  send(json: string): void {
    if (this.socket?.readyState !== WebSocket.OPEN) {
      throw new Error(`${this.label} is not connected.`);
    }
    this.socket.send(json);
  }

  dispose(): void {
    this.disposed = true;
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
    }
    this.socket?.close();
    this.socket = null;
    clearNotification(connectionNotificationKey(this.backendId));
    clearBackendPhase(this.backendId);
  }

  private connect(): void {
    void this.resolveEndpoint().then(
      (endpoint) => {
        setResourceBase(this.backendId, endpoint.resourceBase);
        this.open(endpoint.bridgeUrl);
      },
      () => this.dropped(),
    );
  }

  private open(url: string): void {
    if (this.disposed) {
      return;
    }
    const socket = new WebSocket(url);
    this.socket = socket;
    socket.onopen = (): void => {
      this.reconnectDelayMs = 500;
      this.opened = true;
      void hostConnection(this.backendId)
        ?.connect()
        .then(() => {
          if (this.socket === socket) {
            setBackendPhase(this.backendId, "online");
            clearNotification(connectionNotificationKey(this.backendId));
          }
        })
        .catch(() => socket.close());
    };
    socket.onmessage = (event: MessageEvent): void => {
      if (this.socket === socket && typeof event.data === "string") {
        try {
          const complete = this.messages.ingest(event.data);
          if (complete !== null) {
            receiveRaw(this.backendId, complete);
          }
        } catch (error) {
          reportError(this.backendId, error);
          socket.close();
        }
      }
    };
    socket.onclose = (): void => {
      if (this.socket !== socket) {
        return;
      }
      this.socket = null;
      this.messages.reset();
      hostConnection(this.backendId)?.transportDropped();
      if (!this.disposed) {
        this.dropped();
      }
    };
    socket.onerror = (): void => socket.close();
  }

  private dropped(): void {
    if (this.disposed) {
      return;
    }
    setBackendPhase(this.backendId, "reconnecting");
    notify(
      this.opened ? "error" : "warn",
      this.opened
        ? `Lost connection to ${this.label}. Reconnecting…`
        : `Can't reach ${this.label}. Retrying…`,
      connectionNotificationKey(this.backendId),
    );
    const delay = this.reconnectDelayMs;
    this.reconnectDelayMs = Math.min(this.reconnectDelayMs * 2, 10_000);
    this.reconnectTimer = setTimeout(() => this.connect(), delay);
  }
}

function setResourceBase(backendId: string, resourceBase: string): void {
  setResourceBases((current) => ({ ...current, [backendId]: resourceBase }));
}

function resolveBridgeEndpoint(): BackendEndpoint | null {
  const override = new URLSearchParams(window.location.search).get("weavie-bridge");
  const configured = override ?? window.__WEAVIE_BRIDGE_WS__ ?? "";
  if (configured === "") {
    return null;
  }
  if (configured === "auto") {
    const scheme = window.location.protocol === "https:" ? "wss:" : "ws:";
    return {
      bridgeUrl: `${scheme}//${window.location.host}/weavie-bridge`,
      resourceBase:
        window.__WEAVIE_RESOURCE_BASE__ ??
        `${window.location.protocol}//${window.location.host}/weavie-media`,
    };
  }
  const bridge = new URL(configured);
  const resource = new URL(configured);
  resource.protocol = bridge.protocol === "wss:" ? "https:" : "http:";
  resource.pathname = "/weavie-media";
  return { bridgeUrl: configured, resourceBase: resource.toString() };
}

export function connectBackend(
  id: string,
  name: string,
  resolveEndpoint: () => Promise<BackendEndpoint>,
): void {
  if (backends.has(id)) {
    return;
  }
  const transport = new WebSocketTransport(id, name, resolveEndpoint);
  addBackend({ id, name, isLocal: false }, transport);
  transport.start();
}

export function disconnectBackend(id: string): void {
  if (id === LOCAL_BACKEND_ID) {
    return;
  }
  const backend = backends.get(id);
  if (backend === undefined) {
    return;
  }
  removeHostFeatures(backend.connection);
  backend.transport.dispose();
  backend.connection.disconnect();
  backends.delete(id);
  if (preferredSelection?.backendId === id) {
    const fallback = defaultSession();
    if (fallback !== null) {
      rememberSelection(fallback);
    }
  }
  setResourceBases((current) => {
    const { [id]: _removed, ...rest } = current;
    return rest;
  });
  reconcileSessions();
  setBackendList([...backends.values()].map((candidate) => candidate.info));
  for (const listener of disconnectedListeners) {
    runSafely(id, () => listener(id));
  }
}

function runSafely(backendId: string, action: () => void): void {
  try {
    action();
  } catch (error) {
    reportError(backendId, error);
  }
}

function reportError(backendId: string, error: unknown): void {
  const message = error instanceof Error ? error.message : String(error);
  console.error(`[bridge:${backendId}] ${message}`);
}

(() => {
  window.__weavieReceive = (raw: string): void => receiveRaw(LOCAL_BACKEND_ID, raw);
  window.chrome?.webview?.addEventListener("message", (event) => {
    if (typeof event.data === "string") {
      receiveRaw(LOCAL_BACKEND_ID, event.data);
    }
  });

  let transport: BridgeTransport | null = null;
  if (
    window.webkit?.messageHandlers?.weavie !== undefined ||
    window.chrome?.webview !== undefined
  ) {
    transport = new NativeTransport();
    if (window.__WEAVIE_RESOURCE_BASE__ !== undefined) {
      setResourceBase(LOCAL_BACKEND_ID, window.__WEAVIE_RESOURCE_BASE__);
    }
  } else {
    const endpoint = resolveBridgeEndpoint();
    if (endpoint !== null) {
      transport = new WebSocketTransport(LOCAL_BACKEND_ID, "the Weavie host", () =>
        Promise.resolve(endpoint),
      );
      browserHostedShell = true;
    }
  }
  if (transport !== null) {
    addBackend({ id: LOCAL_BACKEND_ID, name: "default", isLocal: true }, transport);
    if (transport instanceof WebSocketTransport) {
      transport.start();
    }
  }
})();

export function hostInjected<T>(name: string, value: T | undefined, devFallback: T): T {
  if (value !== undefined) {
    return value;
  }
  if (import.meta.env.DEV) {
    return devFallback;
  }
  throw new Error(
    `${name} was not injected by the host before navigation; the host must set it before the web app loads.`,
  );
}
