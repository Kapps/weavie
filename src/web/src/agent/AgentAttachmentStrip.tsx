import { For, type JSX, Show } from "solid-js";

export type AgentAttachmentViewStatus = "reading" | "transferring" | "ready" | "failed";

export interface AgentAttachmentView {
  id: string;
  previewUrl: string;
  status: AgentAttachmentViewStatus;
  error: string | null;
}

/** Shared image previews for active and not-yet-created agent sessions. */
export function AgentAttachmentStrip(props: {
  attachments: AgentAttachmentView[];
  onRemove: (id: string) => void;
}): JSX.Element {
  return (
    <div class="agent-attachments">
      <For each={props.attachments}>
        {(attachment) => (
          <div
            class="agent-attachment"
            classList={{ failed: attachment.status === "failed" }}
            title={attachment.error ?? attachmentLabel(attachment.status)}
          >
            <img src={attachment.previewUrl} alt="Pasted attachment" />
            <Show when={attachment.status !== "ready"}>
              <span>{attachmentLabel(attachment.status)}</span>
            </Show>
            <button
              type="button"
              title="Remove attachment"
              onClick={() => props.onRemove(attachment.id)}
            >
              ×
            </button>
          </div>
        )}
      </For>
    </div>
  );
}

function attachmentLabel(status: AgentAttachmentViewStatus): string {
  switch (status) {
    case "reading":
      return "reading…";
    case "transferring":
      return "uploading…";
    case "failed":
      return "failed";
    default:
      return "ready";
  }
}
