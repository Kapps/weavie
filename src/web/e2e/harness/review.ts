import type { Locator, Page } from "@playwright/test";
import type { FakeStep } from "./fake-claude";

// Drives a real APPLIED change (the post-turn review surface), unlike the openDiff proposal seam diff.spec.ts
// uses. The change tracker is hook-driven, so an applied edit is three steps: PreToolUse snapshots the on-disk
// baseline, the write lands the new content, PostToolUse records it. `{{WORKSPACE}}` resolves in the fake to
// the session worktree. Compose several for a multi-file review.
export function appliedEdit(relPath: string, content: string): FakeStep[] {
  const file = `{{WORKSPACE}}/${relPath}`;
  const hook = (event: "PreToolUse" | "PostToolUse"): FakeStep => ({
    op: "hook",
    request: {
      hook_event_name: event,
      tool_name: "Edit",
      tool_input: { file_path: file },
      cwd: "{{WORKSPACE}}",
    },
  });
  return [hook("PreToolUse"), { op: "edit", path: file, content }, hook("PostToolUse")];
}

/** The review strip's segment for a repo-relative path; clicking it jumps the review to that file. */
export function reviewFileSegment(scope: Page | Locator, path: string): Locator {
  return scope.locator(`.unified-review-map-file[title^="${path} +"]`);
}
