import { type ClientSession, selectedSession } from "../bridge";
import { basename } from "./fs-path";
import {
  type ActivateResult,
  activateTabFor,
  activeTabFor,
  captureViewStateFor,
  closeTabFor,
  openTabFor,
  openTabsFor,
  promoteFor,
  tabOwnerFor,
  togglePinFor,
} from "./session-store";
import type { EditorSessionEntry } from "./session-types";
import { tabKind } from "./tab-entry";
import type { TabOwner } from "./tab-owner";

/** Capture once for a command or menu. Confirmations cannot expand the captured set of owners. */
export function createTabActions(deps: {
  depart(session: ClientSession): void;
  present(session: ClientSession, result: ActivateResult | null): void;
  capture(tab: TabOwner): void;
  content(tab: TabOwner): string;
  release(tab: TabOwner): void;
  confirmDiscard(names: string[]): Promise<boolean>;
}) {
  const closed = new WeakMap<ClientSession, EditorSessionEntry[]>();
  const remember = (tab: TabOwner): void => {
    if (tab.entry.scratch || tabKind(tab.entry) === "plan") return;
    const entries = closed.get(tab.session) ?? [];
    entries.push({ ...tab.entry, preview: false });
    closed.set(tab.session, entries);
  };
  const close = async (targets: TabOwner[], protectPinned: boolean): Promise<void> => {
    const eligible = (tab: TabOwner): boolean =>
      !tab.signal.aborted && !tab.session.signal.aborted && (!protectPinned || !tab.entry.pinned);
    const doomed = targets.filter(eligible);
    const confirmed = new Set<TabOwner>();
    while (true) {
      const dirty = doomed.filter(
        (tab) =>
          eligible(tab) &&
          tab.entry.scratch &&
          !confirmed.has(tab) &&
          deps.content(tab).trim().length > 0,
      );
      if (dirty.length === 0) break;
      if (!(await deps.confirmDiscard(dirty.map((tab) => basename(tab.entry.path))))) return;
      for (const tab of dirty) confirmed.add(tab);
    }
    const session = doomed[0]?.session;
    if (session === undefined) return;
    const active = activeTabFor(session);
    if (active !== undefined) {
      deps.capture(active);
      if (doomed.includes(active) && eligible(active)) deps.depart(session);
    }
    let next: ActivateResult | null = null;
    let changed = false;
    for (const tab of doomed.filter(eligible)) {
      remember(tab);
      const result = closeTabFor(session, tab.entry.path);
      if (result === null) continue;
      next = result.next;
      changed = true;
      deps.release(tab);
    }
    if (changed && active?.signal.aborted) deps.present(session, next);
  };
  const activate = (tab: TabOwner | undefined): boolean => {
    if (tab === undefined) return false;
    tab.assertLive();
    if (selectedSession() !== tab.session)
      throw new Error("The tab's session is no longer displayed.");
    deps.depart(tab.session);
    const result = activateTabFor(tab.session, tab.entry.path);
    deps.present(tab.session, result);
    return true;
  };
  return {
    capture(session: ClientSession, path: string | undefined) {
      const list = openTabsFor(session).map((entry) => tabOwnerFor(session, entry.path)!);
      const target = path === undefined ? activeTabFor(session) : tabOwnerFor(session, path);
      const index = target === undefined ? -1 : list.indexOf(target);
      const stack = closed.get(session) ?? [];
      const reopen = stack.findLast((entry) => tabOwnerFor(session, entry.path) === undefined);
      const withTarget = (action: (tab: TabOwner) => void): boolean => {
        if (target === undefined) return false;
        target.assertLive();
        action(target);
        return true;
      };
      return {
        target,
        activate: () => activate(target),
        close: () => close(target === undefined ? [] : [target], false),
        closeAll: () => close(list, true),
        closeOthers: () =>
          close(target === undefined ? [] : list.filter((tab) => tab !== target), true),
        closeToLeft: () => close(index < 0 ? [] : list.slice(0, index), true),
        closeToRight: () => close(index < 0 ? [] : list.slice(index + 1), true),
        togglePin: () => withTarget((tab) => togglePinFor(session, tab.entry.path)),
        promote: () => withTarget((tab) => promoteFor(session, tab.entry.path)),
        next: () => list.length > 1 && index >= 0 && activate(list[(index + 1) % list.length]),
        prev: () =>
          list.length > 1 && index >= 0 && activate(list[(index - 1 + list.length) % list.length]),
        reopenClosed: () => {
          if (
            reopen === undefined ||
            !stack.includes(reopen) ||
            tabOwnerFor(session, reopen.path) !== undefined
          )
            return false;
          if (selectedSession() !== session)
            throw new Error("The tab's session is no longer displayed.");
          stack.splice(stack.indexOf(reopen), 1);
          deps.depart(session);
          const result = openTabFor(session, reopen.path, { kind: tabKind(reopen) });
          captureViewStateFor(session, result.path, reopen.viewState);
          result.placement = { viewState: reopen.viewState };
          deps.present(session, result);
          return true;
        },
      };
    },
  };
}

export type TabActions = ReturnType<typeof createTabActions>;
