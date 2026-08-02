import { type JSX, Show } from "solid-js";
import type { RailSession } from "../chrome/session-store";
import { type MobileSurface, MobileSurfaceBar } from "./MobileSurfaceBar";
import { SessionInbox } from "./SessionInbox";

/** Compact chrome around the app's one shared, permanently mounted LayoutView. */
export function MobileWorkspace(props: {
  surface: MobileSurface;
  sessions: RailSession[];
  initialBackendId: string;
  initialProviderId: "claude" | "codex";
  onOpen: (session: RailSession) => Promise<boolean>;
  onCreate: (prompt: string, backendId: string, providerId: "claude" | "codex") => Promise<boolean>;
  onMore: () => void;
  moreTitle: string;
  onSurface: (surface: MobileSurface) => void;
  surfaceTitle: (surface: MobileSurface, label: string) => string;
}): JSX.Element {
  return (
    <>
      <Show when={props.surface === "inbox"}>
        <SessionInbox
          sessions={props.sessions}
          initialBackendId={props.initialBackendId}
          initialProviderId={props.initialProviderId}
          onOpen={props.onOpen}
          onCreate={props.onCreate}
          onMore={props.onMore}
          moreTitle={props.moreTitle}
        />
      </Show>
      <MobileSurfaceBar
        active={props.surface}
        onSelect={props.onSurface}
        titleOf={props.surfaceTitle}
      />
    </>
  );
}
