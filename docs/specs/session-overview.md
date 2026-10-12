# Session overview

Feature 7 of the [PR review roadmap](pr-review-roadmap.md). Reviewing starts with context: the review tab opens on the
session's overview, and the diffs follow it in the same scroll. Per *Sessions, not PRs*, every session has one; a PR
only adds sections.

## Layout

The overview replaces the file tree at the top of the review tab and contains it as **Changes**. Scrolling down
reaches the diffs; there is no separate "start" step.

| Section | Shown when | Content |
|---|---|---|
| Title | always | PR title, `#N`, state, author, link, or the branch name; [Finish review](pr-review-finish.md) top-right |
| Description, Conversation | the branch has a PR | PR body; PR-level comments and review events, with a composer |
| Checks, Reviewers | the branch has a PR | failing checks first; each reviewer's latest state |
| Commits | the review compares against a branch (a PR or Diff Against) | `merge-base..HEAD`, scrolling past ten rows |
| Changes | always | the review's file tree, headed by what it compares (`PR #12`, `vs main`, or agent edits) |

Commits appear only for a branch comparison so the overview never pairs "4 commits" with a file list that isn't
those commits. Once the overview's title scrolls away, the sticky review header carries the session's identity
(state, title, failing checks) and the Finish review control, with an **Overview** button back to the top.

**Show Session Overview** (`weavie.review.overview`, unbound) opens it for any session, changes or not, and is how
Finish review reaches its control. Finishing a review still closes the tab and returns to the file underneath.

## Opening a diff

Every new diff opens on the overview: Open Pull Request, Diff Against…, and **Review Branch**
(`weavie.diff.againstBranchBase`, unbound), which compares the branch with the remote's default branch
(`origin/main` over a local `main` that may be far behind). Clicking the status bar's diff totals offers Current
review, Uncommitted, Last commit, and Branch. A session holds one review, so switching to a different comparison asks
first: it closes the current review the way Close Diff does.

## Collapsing

Every section collapses. `editor.reviewOverviewCollapse` decides what remembers it: `global` (default; one set for all
sessions, host-owned in `~/.weavie/review-overview-state.json` like the search and rail state) or `session` (saved
with the session's review). The setting has no UI beyond the settings registry.

## Data

```mermaid
flowchart LR
  W[GitMetadataWatcher] --> C[review commits monitor]
  R[active ReviewContext] --> C
  C -->|review/commits| Web
  M[PullRequestStatusMonitor] -->|target| O[PullRequestOverview]
  P[pullRequests/refresh while visible] --> O
  O -->|one GraphQL query| GH[IPullRequestDiscussion]
  O -->|pullRequests/overview| Web
  Web -->|pullRequests/issueComment| O
```

- **Commits** come from local git: the active review's merge-base to `HEAD`, refreshed when `HEAD` moves or the
  review changes.
- **PR data** is one GraphQL query per refresh (detail, reviewers, conversation, check rollup), loaded when the
  branch's PR or head changes and on the same visible-session poll that refreshes PR comments. A transient failure
  keeps the last good overview for the same PR and shows the error.
- **The composer** posts a PR-level comment immediately; GitHub never files those into a review.
