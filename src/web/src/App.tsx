import {
  type JSX,
  Show,
  Suspense,
  createEffect,
  createMemo,
  createSignal,
  lazy,
  onCleanup,
  onMount,
} from "solid-js";
import { type SessionStatusName, type TermSession, onHostMessage, postToHost } from "./bridge";
import type { ChangeFile } from "./changes/ChangesPanel";
import { DeleteSessionDialog, type DeleteSessionState } from "./chrome/DeleteSessionDialog";
import { MacTitleBar } from "./chrome/MacTitleBar";
import { NewSessionPrompt } from "./chrome/NewSessionPrompt";
import { ResizeFrame } from "./chrome/ResizeFrame";
import { SessionRail } from "./chrome/SessionRail";
import { TitleBar } from "./chrome/TitleBar";
import { focusOmnibar } from "./chrome/omnibar-controller";
// Named imports keep the session store loaded at top level (out of any hot-swapping component) so the
// rail + active-session status survive HMR, the same way layout/store and editor/session-store do.
import { claudeStatus, sessions } from "./chrome/session-store";
import { setContext } from "./commands/context";
import { installDoubleShift } from "./commands/double-shift";
import { installKeybindings } from "./commands/keybindings";
import { dispatchCommand, registerCommand } from "./commands/registry";
import { CommandIds } from "./commands/types";
import { ConfirmDialog } from "./editor/ConfirmDialog";
import { EditorEmptyState } from "./editor/EditorEmptyState";
import { TabStrip } from "./editor/TabStrip";
import { createEditorController } from "./editor/editor-controller";
// Side-effect import: registers the editor session store's set-editor-session listener at top-level module
// load — BEFORE main.tsx posts "ready" and the host replies with its one-shot restore push. The store
// otherwise lives only in the dynamically-imported editor chunk (via editor-host), which loads seconds
// later, so the push would arrive with no listener and be dropped — launch/Ctrl+R restore would silently
// no-op. Importing it here (like layout/store) also keeps the signal alive across HMR.
// Named import keeps session-store loaded at top level (out of the editor chunk) so it survives HMR.
import { activePath, flushEditorSession, openTabs } from "./editor/session-store";
import type { DirListings } from "./files/FileBrowser";
import { LayoutView } from "./layout/LayoutView";
import { paneOrder } from "./layout/geometry";
import { DEFAULT_LAYOUT_ROOT, layoutDocument, sendLayout } from "./layout/store";
import type { LayoutNode } from "./layout/types";
import { Toasts, createToasts } from "./notify/Toasts";
import { mark } from "./startup-timing";
import { TerminalView } from "./terminal/TerminalView";
import { applyChromeTheme } from "./theme";

const ChangesPanel = lazy(() => import("./changes/ChangesPanel"));
const FileBrowser = lazy(() => import("./files/FileBrowser"));

// The PRIMARY session's workspace root (host-injected before navigation). Used to SEED the active-root
// signal (indexRoot) and as the "is there a host workspace at all" check; the live root follows the active
// session via the host's file-index pushes (see indexRoot). Null in plain-browser dev (no host).
const WORKSPACE_ROOT = window.__WEAVIE_LSP__?.workspace ?? null;

// Host-injected shell config. Windows injects titleBar "custom" (the frameless web title bar with its own
// window controls); macOS injects titleBar "mac" (the omnibar strip below the native title bar + system
// menu). Absent in plain-browser dev, where neither bar renders and the floating Files/Changes buttons are
// the panel toggles.
const SHELL = window.__WEAVIE_SHELL__;
const CUSTOM_TITLEBAR = SHELL?.titleBar === "custom";
const MAC_TITLEBAR = SHELL?.titleBar === "mac";
// Either title-bar mode renders the omnibar + view toggles, so the floating panel buttons aren't needed.
const HAS_TITLEBAR = CUSTOM_TITLEBAR || MAC_TITLEBAR;

// Modifier label for the pane-switch shortcut badge: the ⌃ glyph on macOS, "Ctrl+" elsewhere.
const CTRL_LABEL = /Mac/i.test(navigator.userAgent) ? "⌃" : "Ctrl+";

// Human-readable tooltip for each Claude status, shown on the pane-head status dot.
const STATUS_LABEL: Record<SessionStatusName, string> = {
  starting: "Claude is starting",
  working: "Claude is working",
  needsInput: "Claude needs your input",
  idle: "Claude is idle",
  error: "Claude crashed",
};

export default function App(): JSX.Element {
  let editorContainer!: HTMLDivElement;
  // The live pane layout tree: seeded with the default, replaced when the host pushes the persisted
  // layout, and updated optimistically while the user drags a splitter.
  const [layoutRoot, setLayoutRoot] = createSignal<LayoutNode>(DEFAULT_LAYOUT_ROOT);
  // The pane that currently has keyboard focus (tracked from focusin), for the active highlight.
  const [focusedKind, setFocusedKind] = createSignal<string | null>(null);
  // Pane kinds in DFS order; index + 1 is the pane's Ctrl+N number.
  const paneNumbers = createMemo(() => paneOrder(layoutRoot()));
  const numberOf = (kind: string): number => paneNumbers().indexOf(kind) + 1;
  // A terminal registers its focus fn here on mount (the editor focuses via the controller directly).
  const terminalFocus = new Map<string, () => void>();

  const [changeFiles, setChangeFiles] = createSignal<ChangeFile[]>([]);
  // The active session's Claude status (pane-head dot) and the window's sessions (left rail) both live in
  // chrome/session-store as top-level signals so they survive HMR — see that module. The host pushes
  // session-status / session-list on `ready` and on every change; an HMR re-posts neither.
  // Whether the "New session" prompt (branch name + base) is open; the rail's "+" opens it.
  const [newSessionOpen, setNewSessionOpen] = createSignal(false);
  const [changesOpen, setChangesOpen] = createSignal(false);
  const [dirListings, setDirListings] = createSignal<DirListings>({});
  const [browserOpen, setBrowserOpen] = createSignal(false);
  // The file currently shown in the editor, tracked so the browser can highlight + reveal it.
  const [currentFile, setCurrentFile] = createSignal<string | null>(null);
  // User-facing toasts (e.g. an autosave write that failed) — surfaced rather than silently dropped.
  const { toasts, addToast, dismissToast } = createToasts();
  // A pending "discard unsaved scratch?" confirm: the names to discard + the promise resolver the dialog
  // settles. The editor controller routes every tab close through this guard (confirmDiscard below).
  const [confirmReq, setConfirmReq] = createSignal<{
    names: string[];
    resolve: (ok: boolean) => void;
  } | null>(null);
  const confirmDiscard = (names: string[]): Promise<boolean> =>
    new Promise<boolean>((resolve) => setConfirmReq({ names, resolve }));
  const settleConfirm = (ok: boolean): void => {
    const req = confirmReq();
    if (req !== null) {
      setConfirmReq(null);
      req.resolve(ok);
    }
  };
  // Custom title bar state: window chrome (maximize glyph + blur dim) pushed by the host, plus the flat
  // workspace file index the omnibar's "Go to File" filters over and the file browser's tree. indexRoot is
  // the ACTIVE session's worktree root — it follows session switches (the host re-pushes file-index on each),
  // seeded from the primary's WORKSPACE_ROOT until the first push. Both the omnibar and the browser root here.
  const [maximized, setMaximized] = createSignal(false);
  const [windowFocused, setWindowFocused] = createSignal(true);
  const [fileIndex, setFileIndex] = createSignal<string[]>([]);
  const [indexRoot, setIndexRoot] = createSignal<string | null>(WORKSPACE_ROOT);

  // The Monaco editor + all diff/review orchestration; App feeds it host messages and commands.
  const editor = createEditorController({
    onSaveError: (message) => addToast("error", message),
    onCurrentFileChanged: setCurrentFile,
    confirmDiscard,
  });

  const focusPane = (kind: string): void => {
    if (kind === "editor") {
      editor.focusEditor();
      return;
    }
    terminalFocus.get(kind)?.();
  };

  // Switch to a session by id. Flushes the outgoing session's pending (debounced) editor session before
  // the switch so its tab set isn't lost; the host processes both messages in order on the still-active
  // session. Used by the rail (click) and the next/prev keyboard commands alike.
  const switchToSession = (id: string): void => {
    flushEditorSession();
    postToHost({ type: "switch-session", id });
  };

  // Step the active session to the next/prev LOADED chip on the rail (delta ±1, wraps around). Dormant chips
  // are deliberately skipped — a parked session shouldn't sit in the cycle; reach it by clicking or Switch
  // Session… instead. Returns whether it stepped, so with <2 loaded sessions (nothing to switch to) the
  // keystroke falls through, matching tab next/prev.
  const stepSession = (delta: number): boolean => {
    const list = sessions().filter((s) => s.loaded);
    const current = list.findIndex((s) => s.active);
    if (list.length < 2 || current < 0) {
      return false;
    }
    const target = list[(current + delta + list.length) % list.length];
    if (target === undefined) {
      return false;
    }
    switchToSession(target.id);
    return true;
  };

  // A pending session delete, opened only once the host classifies the worktree. The deletePrompt command sends
  // a delete-session-request; the host replies with session-delete-prompt carrying the worktree's state, which
  // opens DeleteSessionDialog with the matching confirm (clean / untracked / modified).
  const [deleteReq, setDeleteReq] = createSignal<{
    id: string;
    label: string;
    state: DeleteSessionState;
  } | null>(null);
  // The interactive delete (rail menu / palette): with no `id` arg it targets the active session. Asks the host
  // to classify the worktree; the session-delete-prompt reply opens the dialog.
  const promptDeleteSession = (args: unknown): void => {
    const id = (args as { id?: string } | undefined)?.id ?? sessions().find((s) => s.active)?.id;
    if (id !== undefined) {
      postToHost({ type: "delete-session-request", id });
    }
  };
  const confirmDeleteSession = (): void => {
    const req = deleteReq();
    if (req === null) {
      return;
    }
    setDeleteReq(null);
    // A dirty worktree (untracked or modified) needs force, or git refuses the removal.
    postToHost({ type: "delete-session", id: req.id, force: req.state !== "clean" });
  };

  // Persist the layout after a user gesture (debounced). Skipped until the host has pushed the initial
  // layout, so we never overwrite the saved state with the default before it has loaded.
  let persistTimer = 0;
  const persistRoot = (root: LayoutNode): void => {
    const base = layoutDocument();
    if (base === null) {
      return;
    }
    window.clearTimeout(persistTimer);
    persistTimer = window.setTimeout(() => {
      sendLayout({ ...base, root });
    }, 400);
  };

  // A splitter drag: show the new sizes immediately, persist on a debounce.
  const onLayoutResize = (root: LayoutNode): void => {
    setLayoutRoot(root);
    persistRoot(root);
  };

  // Apply the layout the host pushes (startup restore + any later host/MCP change). The resize handler
  // is gesture-driven, so applying a pushed layout never echoes back into a save.
  createEffect(() => {
    const doc = layoutDocument();
    if (doc !== null) {
      setLayoutRoot(doc.root);
    }
  });

  // Renders the surface for a pane kind. Called once per kind by LayoutView (the slot list is stable),
  // so the editor and terminals are created a single time and only repositioned thereafter.
  const renderPane = (kind: string): JSX.Element => {
    if (kind === "editor") {
      return (
        <div
          class="editor-surface"
          classList={{ active: focusedKind() === "editor" }}
          data-kind="editor"
        >
          <TabStrip tabs={openTabs} activePath={activePath} actions={editor.tabs} />
          <div class="editor-pane">
            <div class="editor" ref={editorContainer} />
            {/* No file open: cover the blank Monaco host with an identity + keyboard-first starter actions. */}
            <Show when={openTabs().length === 0}>
              <EditorEmptyState />
            </Show>
          </div>
          {/* Pane-switch badge: top-right of the PANE (over the tab strip), not over the editor content. */}
          <span class="pane-shortcut editor-badge">
            {CTRL_LABEL}
            {numberOf("editor")}
          </span>
        </div>
      );
    }
    const session: TermSession = kind === "terminal:claude" ? "claude" : "shell";
    return (
      <div class="terminal-surface" classList={{ active: focusedKind() === kind }} data-kind={kind}>
        <div class="pane-head">
          <span class="pane-label">{kind === "terminal:claude" ? "Claude Code" : "Terminal"}</span>
          <Show when={kind === "terminal:claude" && claudeStatus() !== undefined}>
            <span
              class={`session-status status-${claudeStatus()}`}
              title={STATUS_LABEL[claudeStatus() as SessionStatusName]}
            />
          </Show>
          <span class="pane-shortcut">
            {CTRL_LABEL}
            {numberOf(kind)}
          </span>
        </div>
        <div class="pane-body">
          <TerminalView session={session} onReady={(focus) => terminalFocus.set(kind, focus)} />
        </div>
      </div>
    );
  };

  const toggleBrowser = (): void => {
    setBrowserOpen((open) => !open);
  };

  // Whenever the browser is open and the active session's root listing hasn't loaded, request it; the current
  // file's ancestor folders then cascade open from there. Keyed on indexRoot() (the ACTIVE session's worktree,
  // re-pushed by the host on a switch), not the page-load primary root — so the browser follows the session.
  createEffect(() => {
    const root = indexRoot();
    if (browserOpen() && root !== null && dirListings()[root] === undefined) {
      postToHost({ type: "list-dir", path: root });
    }
  });

  onMount(() => {
    // Apply the active theme to Weavie's chrome (spec §6 application surface). The controller owns the
    // active theme + override ops and also drives Monaco + xterm; this pushes the chrome's CSS vars.
    applyChromeTheme();
    mark("shell-mounted");

    // The terminal panes are already in the tree and mount now — spawning claude — without waiting on
    // Monaco. The editor (a separate chunk, off the first-paint path) is brought up here; the pane shows a
    // placeholder until it resolves, with the splash held over everything until it settles.
    editor.start(editorContainer);

    const offHost = onHostMessage((message) => {
      if (editor.handleMessage(message)) {
        return;
      }
      if (message.type === "notify") {
        addToast(message.level, message.message);
      } else if (message.type === "focus-pane") {
        // The host switched the active session (new / select / close) and asks us to land keyboard focus
        // in a pane — Claude by default, so a switch drops the user straight into the agent. The terminal
        // xterms are persistent across switches, so focusing the slot is valid even mid-respawn.
        focusPane(message.kind);
      } else if (message.type === "session-changes") {
        setChangeFiles(message.files);
      } else if (message.type === "dir-listing") {
        setDirListings((prev) => ({ ...prev, [message.path]: message.entries }));
      } else if (message.type === "window-state") {
        setMaximized(message.maximized);
        setWindowFocused(message.focused);
      } else if (message.type === "file-index") {
        // A switch re-pushes the index rooted at the new session's worktree. When the root changes, drop the
        // previous session's cached directory listings so the file browser re-lists the new worktree's tree
        // instead of showing the old one (listings are keyed by absolute path, so they'd otherwise linger).
        if (message.root !== indexRoot()) {
          setDirListings({});
        }
        setIndexRoot(message.root);
        setFileIndex(message.files);
      } else if (message.type === "session-delete-prompt") {
        // The host classified the worktree; open the delete dialog with the matching confirm.
        setDeleteReq({ id: message.id, label: message.label, state: message.state });
      }
      // session-status + session-list are owned by chrome/session-store (registered at module load so they
      // survive HMR); they're intentionally not handled here.
    });

    // Commands: register the web-side handlers, then install the capture-phase keybinding resolver. The
    // migrated Ctrl+1–9 (focus pane by index), the omnibar focus shortcuts, the view toggles, and the
    // inline-diff actions all resolve through it; Core commands route to the host. See docs/specs/commands.md.
    // A tab command's optional `path` arg (sent by the tab context menu); absent ⇒ act on the active tab.
    const tabPath = (args: unknown): string | undefined => {
      const path = (args as { path?: unknown } | undefined)?.path;
      return typeof path === "string" ? path : undefined;
    };
    const offCommands = [
      // focus-pane-by-index returns false when there's no pane at that number, so an unbound Ctrl+digit
      // still falls through to the focused xterm/Monaco (preserving the old behavior).
      registerCommand(CommandIds.focusPaneByIndex, (args) => {
        const index = Number((args as { index?: unknown } | undefined)?.index);
        if (!Number.isFinite(index)) {
          return false;
        }
        const kind = paneNumbers()[index - 1];
        if (kind === undefined) {
          return false;
        }
        focusPane(kind);
        return true;
      }),
      registerCommand(CommandIds.toggleFileBrowser, () => toggleBrowser()),
      registerCommand(CommandIds.toggleChanges, () => setChangesOpen((open) => !open)),
      registerCommand(CommandIds.focusOmnibarFiles, () => focusOmnibar("file")),
      registerCommand(CommandIds.focusOmnibarCommands, () => focusOmnibar("command")),
      // The floating diff toolbar buttons route through these same actions, so keybindings / the palette /
      // Claude's runCommand drive the active diff identically. Each returns whether it acted, so an
      // unmatched keybinding (no active diff) falls through to the editor.
      registerCommand(CommandIds.nextChange, () => editor.inline.nextChange()),
      registerCommand(CommandIds.prevChange, () => editor.inline.prevChange()),
      registerCommand(CommandIds.acceptChange, () => editor.inline.accept()),
      registerCommand(CommandIds.rejectChange, () => editor.inline.reject()),
      registerCommand(CommandIds.undoChange, () => editor.inline.undo()),
      // Editor tabs. The targeted commands take an optional `path` (the tab context menu passes the
      // right-clicked tab; keyboard / palette omit it to act on the active tab). next/prev return whether they
      // stepped, so $mod+Tab falls through to the editor when there are <2 tabs.
      registerCommand(CommandIds.closeTab, (args) => editor.tabs.close(tabPath(args))),
      registerCommand(CommandIds.nextTab, () => editor.tabs.next()),
      registerCommand(CommandIds.prevTab, () => editor.tabs.prev()),
      registerCommand(CommandIds.closeAllTabs, () => editor.tabs.closeAll()),
      registerCommand(CommandIds.closeOtherTabs, (args) => editor.tabs.closeOthers(tabPath(args))),
      registerCommand(CommandIds.closeTabsToLeft, (args) => editor.tabs.closeToLeft(tabPath(args))),
      registerCommand(CommandIds.closeTabsToRight, (args) =>
        editor.tabs.closeToRight(tabPath(args)),
      ),
      registerCommand(CommandIds.togglePinTab, (args) => editor.tabs.togglePin(tabPath(args))),
      // New File (scratch buffer) + Save (scratch → name prompt; real file already autosaved).
      registerCommand(CommandIds.newFile, () => editor.newFile()),
      registerCommand(CommandIds.saveFile, () => editor.save()),
      // New Session… (Ctrl+Shift+N / palette / the rail's "+"): open the branch-name prompt.
      registerCommand(CommandIds.newSessionPrompt, () => setNewSessionOpen(true)),
      // Next / Previous Session (Ctrl+Shift+] / Ctrl+Shift+[): cycle the rail, wrapping around.
      registerCommand(CommandIds.nextSession, () => stepSession(1)),
      registerCommand(CommandIds.prevSession, () => stepSession(-1)),
      // Ctrl+Shift+1–9 → switch to the Nth session on the rail (the session analogue of Ctrl+1–9 for
      // panes). Returns false when there's no session at that number, so an unbound chord falls through to
      // the focused xterm/Monaco; consumes the key (returns true) when one exists, even if it's already
      // active (then it's a no-op — the host already focuses Claude on a real switch).
      registerCommand(CommandIds.selectSessionByIndex, (args) => {
        const index = Number((args as { index?: unknown } | undefined)?.index);
        if (!Number.isFinite(index)) {
          return false;
        }
        const target = sessions()[index - 1];
        if (target === undefined) {
          return false;
        }
        if (!target.active) {
          switchToSession(target.id);
        }
        return true;
      }),
      // Interactive delete (rail menu / palette): opens the confirm dialog after the host classifies the
      // worktree. The raw delete (weavie.session.delete) is the programmatic/MCP path.
      registerCommand(CommandIds.deleteSessionPrompt, promptDeleteSession),
    ];
    const offKeybindings = installKeybindings();
    // Double-tapping Shift mirrors $mod+P (Go to File) — a gesture the chord resolver can't express.
    const offDoubleShift = installDoubleShift(() => dispatchCommand(CommandIds.focusOmnibarFiles));

    // Track which pane holds focus (by click, Ctrl+N, or tab) for the active highlight, and publish it as a
    // `when`-context key so command guards (e.g. terminalFocused) can read it.
    const onFocusIn = (event: FocusEvent): void => {
      const slot = (event.target as HTMLElement | null)?.closest("[data-kind]");
      const kind = slot?.getAttribute("data-kind") ?? null;
      setFocusedKind(kind);
      setContext("focusedPane", kind);
      setContext("editorFocused", kind === "editor");
      setContext("terminalFocused", kind?.startsWith("terminal:") ?? false);
    };
    document.addEventListener("focusin", onFocusIn);

    onCleanup(() => {
      window.clearTimeout(persistTimer);
      offKeybindings();
      offDoubleShift();
      for (const off of offCommands) {
        off();
      }
      document.removeEventListener("focusin", onFocusIn);
      offHost();
      editor.dispose();
    });
  });

  return (
    <div class="app">
      <Show when={CUSTOM_TITLEBAR}>
        <TitleBar
          maximized={maximized()}
          focused={windowFocused()}
          files={fileIndex()}
          root={indexRoot()}
          currentFile={currentFile()}
          onWindowControl={(action) => postToHost({ type: "window-control", action })}
          onMenuAction={(action, path) =>
            postToHost(
              path === undefined
                ? { type: "menu-action", action }
                : { type: "menu-action", action, path },
            )
          }
          onToggleFiles={toggleBrowser}
          onToggleChanges={() => setChangesOpen((open) => !open)}
          onOpenFile={(path) => postToHost({ type: "reveal-file", path, line: 1 })}
          onRequestIndex={() => postToHost({ type: "request-file-index" })}
        />
      </Show>
      <Show when={CUSTOM_TITLEBAR}>
        <ResizeFrame maximized={maximized()} />
      </Show>
      <Show when={MAC_TITLEBAR}>
        <MacTitleBar
          files={fileIndex()}
          root={indexRoot()}
          currentFile={currentFile()}
          workspaceLabel={SHELL?.workspaceLabel ?? "weavie"}
          onToggleFiles={toggleBrowser}
          onToggleChanges={() => setChangesOpen((open) => !open)}
          onOpenFile={(path) => postToHost({ type: "reveal-file", path, line: 1 })}
          onRequestIndex={() => postToHost({ type: "request-file-index" })}
        />
      </Show>
      <div class="app-body">
        <SessionRail
          sessions={sessions()}
          onSwitch={switchToSession}
          onNew={() => setNewSessionOpen(true)}
        />
        <LayoutView root={layoutRoot()} renderPane={renderPane} onResize={onLayoutResize} />
      </div>
      <Show when={newSessionOpen()}>
        <NewSessionPrompt
          onCreate={(branch, base) => {
            setNewSessionOpen(false);
            postToHost({ type: "new-session", branch, base });
          }}
          onCancel={() => setNewSessionOpen(false)}
        />
      </Show>
      <Show when={changeFiles().length > 0 && !HAS_TITLEBAR}>
        <button
          type="button"
          class="changes-toggle"
          onClick={() => setChangesOpen((open) => !open)}
        >
          Changes {changeFiles().length}
        </button>
      </Show>
      <Show when={changesOpen()}>
        <Suspense>
          <ChangesPanel
            files={changeFiles()}
            currentFile={currentFile()}
            onSelect={(path) => {
              // Open the file in the live editor and request its session diff, shown inline (no diff viewer).
              postToHost({ type: "reveal-file", path, line: 1 });
              postToHost({ type: "get-change-diff", path });
            }}
            onClose={() => setChangesOpen(false)}
          />
        </Suspense>
      </Show>
      <Show when={indexRoot() !== null && !HAS_TITLEBAR}>
        <button type="button" class="browser-toggle" onClick={toggleBrowser}>
          Files
        </button>
      </Show>
      <Show when={browserOpen() && indexRoot() !== null}>
        <Suspense>
          <FileBrowser
            root={indexRoot()!}
            listings={dirListings()}
            currentFile={currentFile()}
            onExpand={(path) => postToHost({ type: "list-dir", path })}
            onOpen={(path) => postToHost({ type: "reveal-file", path, line: 1 })}
            onClose={() => setBrowserOpen(false)}
          />
        </Suspense>
      </Show>
      <Toasts toasts={toasts()} onDismiss={dismissToast} />
      <Show when={confirmReq()}>
        {(req) => (
          <ConfirmDialog
            title={req().names.length > 1 ? "Discard unsaved files?" : "Discard unsaved file?"}
            body={
              req().names.length > 1
                ? `${req().names.length} unsaved scratch files will be discarded: ${req().names.join(", ")}.`
                : `"${req().names[0]}" has unsaved changes and isn't saved to a file yet. Discard it?`
            }
            confirmLabel="Discard"
            cancelLabel="Cancel"
            onConfirm={() => settleConfirm(true)}
            onCancel={() => settleConfirm(false)}
          />
        )}
      </Show>
      <Show when={deleteReq()}>
        {(req) => (
          <DeleteSessionDialog
            label={req().label}
            state={req().state}
            onConfirm={confirmDeleteSession}
            onCancel={() => setDeleteReq(null)}
          />
        )}
      </Show>
    </div>
  );
}
