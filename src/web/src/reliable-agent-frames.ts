interface AgentFrame {
  type?: unknown;
  slot?: unknown;
  id?: unknown;
  status?: unknown;
}

export class ReliableAgentFrames {
  private readonly pending = new Map<string, string>();

  track(json: string): boolean {
    const frame = readFrame(json);
    if (frame.type === "agent-attachment-remove") {
      const attachment = operationKey("attachment", frame);
      const removal = operationKey("removal", frame);
      if (attachment === null || removal === null) {
        return false;
      }
      this.pending.delete(attachment);
      this.pending.set(removal, json);
      return true;
    }
    const key =
      frame.type === "agent-attachment-upload"
        ? operationKey("attachment", frame)
        : frame.type === "agent-submit"
          ? operationKey("submission", frame)
          : null;
    if (key === null) {
      return false;
    }
    this.pending.set(key, json);
    return true;
  }

  acknowledge(json: string): void {
    const frame = readFrame(json);
    if (frame.type === "agent-attachment-state") {
      const attachment = operationKey("attachment", frame);
      const removal = operationKey("removal", frame);
      if (attachment !== null) {
        this.pending.delete(attachment);
      }
      if (frame.status === "removed" && removal !== null) {
        this.pending.delete(removal);
      }
    } else if (frame.type === "agent-submission-state") {
      const submission = operationKey("submission", frame);
      if (submission !== null) {
        this.pending.delete(submission);
      }
    }
  }

  replay(): string[] {
    return [...this.pending.values()];
  }
}

function readFrame(json: string): AgentFrame {
  try {
    const value: unknown = JSON.parse(json);
    return typeof value === "object" && value !== null ? value : {};
  } catch {
    return {};
  }
}

function operationKey(kind: string, frame: AgentFrame): string | null {
  return typeof frame.slot === "string" &&
    frame.slot.length > 0 &&
    typeof frame.id === "string" &&
    frame.id.length > 0
    ? `${kind}:${frame.slot}:${frame.id}`
    : null;
}
