import { Quote, X } from "lucide-solid";
import { For, type JSX, Match, Show, Switch } from "solid-js";

export type AgentAttachmentViewStatus = "reading" | "transferring" | "ready" | "failed";

export interface AgentImageView {
  kind?: "image";
  id: string;
  previewUrl: string;
  status: AgentAttachmentViewStatus;
  error: string | null;
}

/** Values the user selected in the pane, attached to the next prompt as text. */
export interface AgentSelectionView {
  kind: "selection";
  id: string;
  label: string;
}

export type AgentAttachmentView = AgentImageView | AgentSelectionView;

/** Shared attachment chips for active and not-yet-created agent sessions. */
export function AgentAttachmentStrip(props: {
  attachments: AgentAttachmentView[];
  onRemove: (id: string) => void;
}): JSX.Element {
  return (
    <div class="agent-attachments">
      <For each={props.attachments}>
        {(attachment) => (
          <Switch>
            <Match when={attachment.kind === "selection" && attachment}>
              {(selection) => (
                <div class="agent-attachment agent-attachment-selection" title={selection().label}>
                  <span class="ui-icon">
                    <Quote aria-hidden="true" />
                  </span>
                  <span class="agent-attachment-label">{selection().label}</span>
                  <RemoveButton onRemove={() => props.onRemove(selection().id)} />
                </div>
              )}
            </Match>
            <Match when={attachment.kind !== "selection" && (attachment as AgentImageView)}>
              {(image) => (
                <div
                  class="agent-attachment"
                  classList={{ failed: image().status === "failed" }}
                  title={image().error ?? attachmentLabel(image().status)}
                >
                  <img src={image().previewUrl} alt="Pasted attachment" />
                  <Show when={image().status !== "ready"}>
                    <span>{attachmentLabel(image().status)}</span>
                  </Show>
                  <RemoveButton onRemove={() => props.onRemove(image().id)} />
                </div>
              )}
            </Match>
          </Switch>
        )}
      </For>
    </div>
  );
}

function RemoveButton(props: { onRemove: () => void }): JSX.Element {
  return (
    <button type="button" class="ui-icon-button" title="Remove attachment" onClick={props.onRemove}>
      <X aria-hidden="true" />
    </button>
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
