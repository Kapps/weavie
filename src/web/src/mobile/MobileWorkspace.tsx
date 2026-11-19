import type { JSX } from "solid-js";
import type { ClientSession } from "../bridge";
import type { RailSession } from "../chrome/session-store";
import {
  type MobileSurface,
  MobileSurfaceBar,
  type MobileSwipeDirection,
} from "./MobileSurfaceBar";
import { type NewSessionSeed, SessionInbox } from "./SessionInbox";
import { SessionInboxSurface } from "./SessionInboxSurface";
import type { SessionsOpening } from "./sessions-opening";

/** Compact chrome around the app's one shared, permanently mounted LayoutView. */
export function MobileWorkspace(props: {
  surface: MobileSurface;
  compact: boolean;
  modalOpen: boolean;
  inboxActive: boolean;
  sessions: RailSession[];
  inboxOpening: SessionsOpening;
  onOpen: (session: RailSession) => Promise<boolean>;
  onCreate: (
    seed: () => Promise<NewSessionSeed | null>,
    backendId: string,
    providerId: string,
    repo: string | null,
    source: ClientSession | null,
  ) => Promise<boolean>;
  onManageAcp: (backendId: string) => void;
  onSurface: (surface: MobileSurface) => void;
  onDismiss: () => void;
  onSwipeCancel: () => void;
  onSwipeCommit: () => void;
  onSwipeProgress: (
    target: MobileSurface,
    direction: MobileSwipeDirection,
    progress: number,
  ) => void;
  surfaceTitle: (surface: MobileSurface, label: string) => string;
}): JSX.Element {
  return (
    <>
      <SessionInboxSurface
        compact={props.compact}
        modalOpen={props.modalOpen}
        onDismiss={props.onDismiss}
      >
        <SessionInbox
          active={props.inboxActive}
          compact={props.compact}
          sessions={props.sessions}
          opening={props.inboxOpening}
          onOpen={props.onOpen}
          onCreate={props.onCreate}
          onManageAcp={props.onManageAcp}
        />
      </SessionInboxSurface>
      <MobileSurfaceBar
        active={props.surface}
        onSelect={props.onSurface}
        onSwipeCancel={props.onSwipeCancel}
        onSwipeCommit={props.onSwipeCommit}
        onSwipeProgress={props.onSwipeProgress}
        titleOf={props.surfaceTitle}
      />
    </>
  );
}
