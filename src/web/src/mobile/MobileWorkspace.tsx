import type { JSX } from "solid-js";
import type { RailSession } from "../chrome/session-store";
import {
  type MobileSurface,
  MobileSurfaceBar,
  type MobileSwipeDirection,
} from "./MobileSurfaceBar";
import { type NewSessionSeed, SessionInbox } from "./SessionInbox";
import { SessionInboxSurface } from "./SessionInboxSurface";

/** Compact chrome around the app's one shared, permanently mounted LayoutView. */
export function MobileWorkspace(props: {
  surface: MobileSurface;
  compact: boolean;
  modalOpen: boolean;
  inboxActive: boolean;
  sessions: RailSession[];
  initialBackendId: string;
  initialProviderId: "claude" | "codex";
  onOpen: (session: RailSession) => Promise<boolean>;
  onCreate: (
    seed: NewSessionSeed,
    backendId: string,
    providerId: "claude" | "codex",
  ) => Promise<boolean>;
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
          sessions={props.sessions}
          initialBackendId={props.initialBackendId}
          initialProviderId={props.initialProviderId}
          onOpen={props.onOpen}
          onCreate={props.onCreate}
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
