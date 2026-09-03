import type { DeleteSessionState } from "./delete-session-confirm";

export interface DeleteSessionPreview {
  revision: string;
  label: string;
  removesCheckout: boolean;
  worktree: {
    state: DeleteSessionState;
    branchless: boolean;
    changedFiles: string[];
    changedCount: number;
  };
  drafts: { path: string; name: string }[];
}

export function parseDeleteSessionPreview(value: unknown): DeleteSessionPreview {
  if (typeof value !== "object" || value === null) {
    throw new Error("The host returned an invalid deletion preview.");
  }
  const root = value as Record<string, unknown>;
  const worktree = root.worktree as Record<string, unknown> | undefined;
  if (
    typeof root.revision !== "string" ||
    root.revision.length === 0 ||
    typeof root.label !== "string" ||
    typeof root.removesCheckout !== "boolean" ||
    typeof worktree !== "object" ||
    worktree === null ||
    !["clean", "untracked", "modified"].includes(String(worktree.state)) ||
    typeof worktree.branchless !== "boolean" ||
    !Array.isArray(worktree.changedFiles) ||
    !worktree.changedFiles.every((file) => typeof file === "string") ||
    !Number.isInteger(worktree.changedCount) ||
    Number(worktree.changedCount) < worktree.changedFiles.length ||
    !Array.isArray(root.drafts)
  ) {
    throw new Error("The host returned an invalid deletion preview.");
  }
  const drafts = root.drafts.map((draft) => {
    if (
      typeof draft !== "object" ||
      draft === null ||
      typeof (draft as Record<string, unknown>).path !== "string" ||
      (draft as Record<string, unknown>).path === "" ||
      typeof (draft as Record<string, unknown>).name !== "string" ||
      (draft as Record<string, unknown>).name === ""
    ) {
      throw new Error("The host returned an invalid deletion preview.");
    }
    return {
      path: (draft as Record<string, unknown>).path as string,
      name: (draft as Record<string, unknown>).name as string,
    };
  });
  return {
    revision: root.revision,
    label: root.label,
    removesCheckout: root.removesCheckout,
    worktree: {
      state: worktree.state as DeleteSessionState,
      branchless: worktree.branchless,
      changedFiles: worktree.changedFiles as string[],
      changedCount: worktree.changedCount as number,
    },
    drafts,
  };
}
