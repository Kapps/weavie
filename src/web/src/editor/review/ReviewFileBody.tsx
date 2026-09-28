import type { Accessor } from "solid-js";
import type { ClientSession } from "../../bridge";
import type { ReviewCopy } from "../editor-host";
import type { ReviewScopeState } from "../inline-diff";
import type { TabOwner } from "../tab-owner";
import type { ReviewDocumentScope } from "./review-document";
import type { ReviewEditor } from "./review-editor";
import type { ReviewHorizontalPosition } from "./review-horizontal-position";
import type { ReviewPreparationQueue } from "./review-preparation-queue";
import type { ReviewScroll } from "./review-scroll";
import type { ReviewFileView } from "./review-store";
import type { ReviewSectionRegistry } from "./review-surface";

export { ReviewAdaptiveBody as ReviewFileBody } from "./ReviewAdaptiveBody";

export interface ReviewFileBodyProps {
  session: ClientSession;
  tab: TabOwner;
  onEditor(editor: Pick<ReviewEditor, "layout" | "shift"> | undefined): void;
  activated: () => boolean;
  ownsEditor(): boolean;
  claimEditor(): void;
  preparePassive: ReviewPreparationQueue;
  header: () => HTMLElement;
  scope: ReviewScopeState;
  active: () => boolean;
  controlsChanged(): void;
  onCursor: (line: number) => void;
  file: Accessor<ReviewFileView>;
  scroller: () => ReviewScroll;
  editorHeight: () => number;
  onEditorHeight: (height: number) => boolean;
  measure: () => void;
  openCopy: () => Promise<ReviewCopy>;
  register: ReviewSectionRegistry;
  documents: ReviewDocumentScope;
  horizontal: ReviewHorizontalPosition;
}
