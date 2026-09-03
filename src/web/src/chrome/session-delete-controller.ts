import { createSignal } from "solid-js";
import { clientSession, waitForClientSession } from "../bridge";
import { dispatchCommandFromCatalog } from "../commands/registry";
import { CommandIds, type CommandResult } from "../commands/types";
import type { EditorController } from "../editor/editor-controller";
import { requireSessionAddress } from "../messaging/message-envelope";
import { type DeleteSessionPreview, parseDeleteSessionPreview } from "./session-delete-preview";

export interface DeleteSessionRequest extends DeleteSessionPreview {
  id: string;
  backendId: string;
  busy: boolean;
}

interface SessionDeleteControllerDeps {
  editor: Pick<EditorController, "saveScratchFor">;
  onError: (message: string) => void;
}

export function createSessionDeleteController(deps: SessionDeleteControllerDeps): {
  request: () => DeleteSessionRequest | null;
  open(id: string, backendId: string): Promise<void>;
  cancel(): void;
  confirm(): Promise<void>;
  saveDrafts(): Promise<void>;
} {
  const [request, setRequest] = createSignal<DeleteSessionRequest | null>(null);
  let openSequence = 0;
  const isCurrent = (sequence: number): boolean => sequence === openSequence;

  const preview = async (id: string, backendId: string): Promise<DeleteSessionPreview> => {
    const result = await dispatchCommandFromCatalog(backendId, CommandIds.deleteSession, {
      id,
      operation: "preview",
    });
    if (!result.ok) {
      throw new Error(result.error ?? "Couldn't inspect the session before deletion.");
    }
    return parseDeleteSessionPreview(result.data);
  };

  const open = async (id: string, backendId: string): Promise<void> => {
    const sequence = ++openSequence;
    try {
      const next = await preview(id, backendId);
      if (isCurrent(sequence)) {
        setRequest({ id, backendId, busy: false, ...next });
      }
    } catch (error) {
      if (isCurrent(sequence)) {
        setRequest(null);
        deps.onError(error instanceof Error ? error.message : String(error));
      }
    }
  };

  const cancel = (): void => {
    if (request()?.busy !== true) {
      openSequence += 1;
      setRequest(null);
    }
  };

  const replaceFromResult = (
    current: DeleteSessionRequest,
    result: CommandResult,
    sequence: number,
  ): boolean => {
    if (!isCurrent(sequence)) {
      return false;
    }
    if (result.data === undefined) {
      return false;
    }
    try {
      setRequest({
        id: current.id,
        backendId: current.backendId,
        busy: false,
        ...parseDeleteSessionPreview(result.data),
      });
      return true;
    } catch {
      return false;
    }
  };

  const confirm = async (): Promise<void> => {
    const current = request();
    if (current === null || current.busy) {
      return;
    }
    const sequence = openSequence;
    setRequest({ ...current, busy: true });
    const result = await dispatchCommandFromCatalog(current.backendId, CommandIds.deleteSession, {
      id: current.id,
      operation: "confirm",
      revision: current.revision,
      forceWorktree: current.worktree.state !== "clean" || current.worktree.branchless,
      discardDrafts: current.drafts.length > 0,
    });
    if (!isCurrent(sequence)) {
      return;
    }
    if (result.ok) {
      setRequest(null);
      return;
    }
    if (!replaceFromResult(current, result, sequence)) {
      setRequest({ ...current, busy: false });
    }
    deps.onError(result.error ?? "Couldn't delete the session.");
  };

  const refresh = async (current: DeleteSessionRequest, sequence: number): Promise<void> => {
    try {
      const next = await preview(current.id, current.backendId);
      if (isCurrent(sequence)) {
        setRequest({ id: current.id, backendId: current.backendId, busy: false, ...next });
      }
    } catch (error) {
      if (isCurrent(sequence)) {
        setRequest({ ...current, busy: false });
        deps.onError(error instanceof Error ? error.message : String(error));
      }
    }
  };

  const saveDrafts = async (): Promise<void> => {
    const current = request();
    if (current === null || current.busy || current.drafts.length === 0) {
      return;
    }
    const sequence = openSequence;
    setRequest({ ...current, busy: true });
    let owner = clientSession(current.backendId, current.id);
    if (owner === undefined) {
      const loaded = await dispatchCommandFromCatalog(current.backendId, CommandIds.loadSession, {
        id: current.id,
      });
      if (!isCurrent(sequence)) {
        return;
      }
      if (!loaded.ok) {
        setRequest({ ...current, busy: false });
        deps.onError(loaded.error ?? `Couldn't load ${current.label} to save its drafts.`);
        return;
      }
      try {
        const address = requireSessionAddress(
          (loaded.data as { address?: unknown } | undefined)?.address,
          "Loading the session did not return an exact owner.",
        );
        owner = await waitForClientSession(current.backendId, address);
        if (!isCurrent(sequence)) {
          return;
        }
      } catch (error) {
        if (isCurrent(sequence)) {
          setRequest({ ...current, busy: false });
          deps.onError(error instanceof Error ? error.message : String(error));
        }
        return;
      }
    }

    for (const draft of current.drafts) {
      if (!isCurrent(sequence)) {
        return;
      }
      const outcome = await deps.editor.saveScratchFor(owner, draft.path);
      if (!isCurrent(sequence)) {
        return;
      }
      if (outcome.status !== "saved") {
        if (outcome.status === "failed") {
          deps.onError(outcome.error);
        }
        await refresh(current, sequence);
        return;
      }
    }
    await refresh(current, sequence);
  };

  return { request, open, cancel, confirm, saveDrafts };
}
