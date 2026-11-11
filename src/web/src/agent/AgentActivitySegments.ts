import type { AgentPaneUpdate } from "../bridge";
import { isAgentActivity, ProjectedAgentActivity } from "./AgentPaneActivitySummary";
import { paneActivityIdentity, paneItemIdentity } from "./AgentPaneIdentity";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

export interface AgentActivitySegment {
  entry: AgentTranscriptEntry;
  projection: ProjectedAgentActivity;
  turn: string;
}

/** Items keep their original segment; only new items can join the trailing group. */
export class AgentActivitySegments {
  readonly groups = new Map<string, AgentActivitySegment>();
  private readonly owners = new Map<string, AgentActivitySegment>();
  private trailing: AgentActivitySegment | undefined;

  boundary(): void {
    this.trailing = undefined;
  }

  find(message: AgentPaneUpdate, fallbackTurnId: string): AgentActivitySegment | undefined {
    if (!isAgentActivity(message)) return undefined;
    const identity = paneItemIdentity(message);
    const owner = identity === null ? undefined : this.owners.get(identity);
    if (owner !== undefined) return owner;
    return this.trailing?.turn === paneActivityIdentity(message, fallbackTurnId)
      ? this.trailing
      : undefined;
  }

  add(message: AgentPaneUpdate, fallbackTurnId: string): AgentActivitySegment {
    const existing = this.find(message, fallbackTurnId);
    if (existing !== undefined) return existing;
    const turn = paneActivityIdentity(message, fallbackTurnId);
    const id = `activity-${paneItemIdentity(message) ?? `${turn}:${this.groups.size}`}`;
    const segment: AgentActivitySegment = {
      turn,
      projection: new ProjectedAgentActivity(),
      entry: {
        id,
        kind: "activity",
        label: "Working",
        tone: "activity",
        actionMessage: null,
        detailCount: 0,
        details: [],
        status: null,
        streaming: false,
        summary: null,
        text: null,
      },
    };
    this.groups.set(id, segment);
    this.trailing = segment;
    return segment;
  }

  upsert(segment: AgentActivitySegment, message: AgentPaneUpdate): number {
    const identity = paneItemIdentity(message);
    if (identity !== null) this.owners.set(identity, segment);
    return segment.projection.upsert(message);
  }
}
