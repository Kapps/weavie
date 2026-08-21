import { type Accessor, createSignal } from "solid-js";
import type { AgentAttachmentViewStatus } from "../agent/AgentAttachmentStrip";
import { agentImageError, encodeAgentImage, takePastedImages } from "../agent/pasted-images";
import type { EncodedImageAttachment } from "../bridge";

export type NewSessionSeedAttachment = EncodedImageAttachment;

export interface NewSessionAttachmentDraft extends NewSessionSeedAttachment {
  previewUrl: string;
  status: AgentAttachmentViewStatus;
  error: string | null;
  objectUrl: string | null;
}

export interface NewSessionAttachments {
  attachments: Accessor<NewSessionAttachmentDraft[]>;
  addEncodedImage: (mime: string, dataB64: string) => void;
  capturePaste: (event: ClipboardEvent) => void;
  clear: () => void;
  remove: (id: string) => void;
}

let attachmentSequence = 0;

/** Owns image encoding, validation, and preview lifetimes for the new-session composer. */
export function createNewSessionAttachments(): NewSessionAttachments {
  const [attachments, setAttachments] = createSignal<NewSessionAttachmentDraft[]>([]);
  const nextId = (): string =>
    `new-session-image-${Date.now().toString(36)}-${(++attachmentSequence).toString(36)}`;

  const update = (id: string, values: Partial<NewSessionAttachmentDraft>): void => {
    setAttachments((current) =>
      current.map((attachment) =>
        attachment.id === id ? { ...attachment, ...values } : attachment,
      ),
    );
  };

  const addEncodedImage = (mime: string, dataB64: string): void => {
    const error = agentImageError(mime, dataB64);
    setAttachments((current) => [
      ...current,
      {
        id: nextId(),
        mime,
        dataB64,
        previewUrl: `data:${mime};base64,${dataB64}`,
        status: error === null ? "ready" : "failed",
        error,
        objectUrl: null,
      },
    ]);
  };

  const capturePaste = (event: ClipboardEvent): void => {
    for (const blob of takePastedImages(event)) {
      const id = nextId();
      const objectUrl = URL.createObjectURL(blob);
      setAttachments((current) => [
        ...current,
        {
          id,
          mime: blob.type,
          dataB64: "",
          previewUrl: objectUrl,
          status: "reading",
          error: null,
          objectUrl,
        },
      ]);
      void encodeAgentImage(blob).then(
        (dataB64) => {
          const error = agentImageError(blob.type, dataB64);
          update(id, {
            dataB64,
            status: error === null ? "ready" : "failed",
            error,
          });
        },
        (error: unknown) => {
          update(id, {
            status: "failed",
            error: error instanceof Error ? error.message : String(error),
          });
        },
      );
    }
  };

  const remove = (id: string): void => {
    setAttachments((current) => {
      const removed = current.find((attachment) => attachment.id === id);
      if (removed?.objectUrl !== null && removed?.objectUrl !== undefined) {
        URL.revokeObjectURL(removed.objectUrl);
      }
      return current.filter((attachment) => attachment.id !== id);
    });
  };

  const clear = (): void => {
    for (const attachment of attachments()) {
      if (attachment.objectUrl !== null) {
        URL.revokeObjectURL(attachment.objectUrl);
      }
    }
    setAttachments([]);
  };

  return { attachments, addEncodedImage, capturePaste, clear, remove };
}
