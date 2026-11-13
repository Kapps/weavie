import { ChevronLeft, ChevronRight, ListFilter, MessageSquare } from "lucide-solid";
import { createSignal, type JSX, Show } from "solid-js";
import type { ClientSession } from "../../bridge";
import { ContextMenu, type ContextMenuState } from "../../chrome/ContextMenu";
import { keyHint } from "../../commands/key-hint";
import { captureCommandRunnerFor } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { prCommentsFor } from "../pr-comments/pr-comments-store";
import { reviewHintsFor } from "../review-hints/review-hints-store";
import { reviewItems } from "./review-items";
import { reviewOptions } from "./review-options";
import "./review-items.css";

/** `‹ 3 of 8 ›`: the current item's place among the session's comments and hints, with the step commands. */
export function ItemPosition(props: { session: ClientSession }): JSX.Element {
  const run = captureCommandRunnerFor(props.session, new Map());
  const position = () => reviewItems.position(props.session);
  const step = (id: string, label: string, icon: JSX.Element) => (
    <button
      type="button"
      title={`${label}${keyHint(id)}`}
      aria-label={label}
      disabled={position().total === 0}
      onMouseDown={(event) => event.preventDefault()}
      onClick={() => void run(id, undefined)}
    >
      {icon}
    </button>
  );
  return (
    <span class="ui-stepper review-item-position">
      {step(CommandIds.reviewPreviousItem, "Previous Comment or Hint", <ChevronLeft />)}
      <span>
        {position().index === null
          ? position().total
          : `${position().index} of ${position().total}`}
      </span>
      {step(CommandIds.reviewNextItem, "Next Comment or Hint", <ChevronRight />)}
    </span>
  );
}

/** The unified review header's stepper: position, the step commands, and the filter menu. */
export function ReviewItemStepper(props: { session: ClientSession }): JSX.Element {
  const run = captureCommandRunnerFor(props.session, new Map());
  const [menu, setMenu] = createSignal<ContextMenuState | null>(null);
  const shown = () =>
    prCommentsFor(props.session)?.set != null || (reviewHintsFor(props.session)?.length ?? 0) > 0;
  const openFilter = (anchor: HTMLElement): void => {
    const rect = anchor.getBoundingClientRect();
    const counts = reviewItems.counts(props.session);
    const options = reviewOptions();
    setMenu({
      x: rect.left,
      y: rect.bottom + 4,
      entries: [
        {
          commandId: CommandIds.prToggleComments,
          label: "Comments",
          checked: options.reviewComments,
          detail: String(counts.comments),
        },
        {
          commandId: CommandIds.reviewHintsToggle,
          label: "Hints",
          checked: options.reviewHints,
          detail: String(counts.hints),
        },
        { kind: "separator" },
        {
          commandId: CommandIds.reviewToggleSettledItems,
          label: "Resolved and outdated",
          checked: options.reviewStepSettled,
          detail: String(counts.settled),
        },
      ],
      runCommand: run,
    });
  };
  return (
    <Show when={shown()}>
      <span class="review-item-stepper">
        <span class="ui-icon review-item-icon">
          <MessageSquare />
        </span>
        <ItemPosition session={props.session} />
        <span class="ui-stepper">
          <button
            type="button"
            title="Filter Comments and Hints"
            aria-label="Filter Comments and Hints"
            aria-haspopup="menu"
            onClick={(event) => openFilter(event.currentTarget)}
          >
            <ListFilter />
          </button>
        </span>
      </span>
      <Show when={menu()}>
        {(state) => <ContextMenu menu={state()} onClose={() => setMenu(null)} />}
      </Show>
    </Show>
  );
}
