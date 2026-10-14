import { type JSX, Show } from "solid-js";
import { AgentMarkdown } from "../../../agent/AgentMarkdown";
import type { ClientSession } from "../../../bridge";
import { DiffStats } from "../DiffStats";
import { ReviewFileTree } from "../ReviewFileTree";
import { canCloseReview, type ReviewFileView, type ReviewOverview } from "../review-store";
import { isReviewed } from "../review-tree";
import { OverviewConversation } from "./OverviewConversation";
import { checksSummary, OverviewChecks, OverviewCommits, OverviewReviewers } from "./OverviewLists";
import { OverviewSection } from "./OverviewSection";
import { OverviewTitle } from "./OverviewTitle";
import { sectionCollapsed, setSectionCollapsed } from "./overview-collapse";
import { prOverviewFor, reviewCommitsFor } from "./overview-data";
import "./overview.css";

/**
 * The top of a session's review: what the session is (its PR or branch), the PR's discussion and checks, the branch's
 * commits (dimmed where the review doesn't span them), and its changed files, which lead straight into the diffs below.
 */
export function SessionOverview(props: {
  session: ClientSession;
  overview: () => ReviewOverview;
  files: () => ReviewFileView[];
  current: () => number | undefined;
  displayPath: (path: string) => string;
  onChoose: (index: number) => void;
  setForSession: (session: ClientSession, section: string, collapsed: boolean) => void;
  /** Whether the review-submit control is in the overview (else the review header has it). */
  submit: () => boolean;
  ref: (element: HTMLElement) => void;
  titleRef: (element: HTMLElement) => void;
}): JSX.Element {
  const pr = () => prOverviewFor(props.session);
  const detail = () => pr()?.detail ?? null;
  // Without a review open there's nothing for a commit to fall outside of.
  const reviewing = () => canCloseReview(props.overview());
  const commits = () => {
    const value = reviewCommitsFor(props.session);
    return value === null || (value.commits.length === 0 && value.error === null) ? null : value;
  };
  // Meta is a thunk so the heading's counts stay live; the section itself is created once.
  const section = (id: string, title: string, meta: () => JSX.Element, body: () => JSX.Element) => (
    <OverviewSection
      id={id}
      title={title}
      meta={meta()}
      collapsed={sectionCollapsed(props.overview(), id)}
      onToggle={(collapsed) =>
        setSectionCollapsed(props.session, id, collapsed, props.setForSession)
      }
    >
      {body()}
    </OverviewSection>
  );
  const commitsSection = () =>
    section(
      "commits",
      "Commits",
      () => {
        const all = commits()?.commits ?? [];
        const spanned = all.filter((commit) => commit.inReview).length;
        return !reviewing() || spanned === all.length
          ? all.length
          : `${spanned} of ${all.length} in this review`;
      },
      () => (
        <>
          <Show when={commits()?.error}>
            {(error) => <p class="session-overview-error">{error()}</p>}
          </Show>
          <OverviewCommits commits={commits()?.commits ?? []} reviewing={reviewing()} />
        </>
      ),
    );
  const reviewed = () => props.files().filter(isReviewed).length;
  return (
    <div class="session-overview" ref={props.ref}>
      <OverviewTitle
        session={props.session}
        detail={detail()}
        submit={props.submit()}
        branchReview={reviewCommitsFor(props.session)?.wholeBranch === true}
        ref={props.titleRef}
      />
      <Show when={pr()?.error}>{(error) => <p class="session-overview-error">{error()}</p>}</Show>
      <Show when={detail() !== null || commits() !== null}>
        <div class="session-overview-columns" classList={{ single: detail() === null }}>
          <div class="session-overview-main">
            <Show when={detail()} fallback={commitsSection()}>
              {(pull) => (
                <>
                  <Show when={pull().body.trim() !== ""}>
                    {section(
                      "description",
                      "Description",
                      () => null,
                      () => (
                        <div class="session-overview-description">
                          <AgentMarkdown
                            cacheKey={pull()}
                            content={pull().body}
                            flavor="github"
                            renderMermaid={false}
                            session={props.session}
                          />
                        </div>
                      ),
                    )}
                  </Show>
                  {section(
                    "conversation",
                    "Conversation",
                    () => pull().conversation.length,
                    () => (
                      <OverviewConversation session={props.session} detail={pull()} />
                    ),
                  )}
                </>
              )}
            </Show>
          </div>
          <Show when={detail()}>
            {(pull) => (
              <aside class="session-overview-side">
                {section(
                  "checks",
                  "Checks",
                  () => checksSummary(pull()),
                  () => (
                    <OverviewChecks detail={pull()} />
                  ),
                )}
                {section(
                  "reviewers",
                  "Reviewers",
                  () => null,
                  () => (
                    <OverviewReviewers detail={pull()} />
                  ),
                )}
                <Show when={commits() !== null}>{commitsSection()}</Show>
              </aside>
            )}
          </Show>
        </div>
      </Show>
      {section(
        "changes",
        "Changes",
        () => (
          <>
            {props.overview().label === "" ? "agent edits" : props.overview().label} ·{" "}
            {props.files().length} file{props.files().length === 1 ? "" : "s"}
            <DiffStats added={props.overview().added} removed={props.overview().removed} />
            <Show when={props.files().length > 0}>
              <span>
                · {reviewed()} of {props.files().length} reviewed
              </span>
            </Show>
          </>
        ),
        () => (
          <Show
            when={props.files().length > 0}
            fallback={<p class="session-overview-empty">Nothing to review yet.</p>}
          >
            <ReviewFileTree
              files={props.files}
              current={props.current}
              displayPath={props.displayPath}
              onChoose={props.onChoose}
            />
          </Show>
        ),
      )}
    </div>
  );
}
