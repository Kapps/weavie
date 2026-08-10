import type { AgentPaneUpdate } from "../bridge";
import { MaximumIndex } from "./AgentPaneMaximumIndex";
import { hasItemId, normalizeStatus, normalizeText } from "./AgentPaneMessageFormat";
import type { AgentActivityStep } from "./AgentPaneTranscriptTypes";

export interface AgentActivitySummary {
  status: string | null;
  summary: string | null;
  tone: "activity" | "error";
}

export class ProjectedAgentActivity {
  private readonly sources: AgentPaneUpdate[] = [];
  private readonly indexes = new Map<string, number>();
  private readonly categoryCounts = new Map<string, number>();
  private readonly active = new MaximumIndex();
  private readonly failed = new MaximumIndex();

  get count(): number {
    return this.sources.length;
  }

  canUpsert(message: AgentPaneUpdate): boolean {
    const category = activityCategory(message);
    if (category === null) {
      return false;
    }
    const existing = this.indexes.get(activityStepId(message, category));
    return existing === undefined || activityCategory(this.sources[existing]!) === category;
  }

  upsert(message: AgentPaneUpdate): number {
    const category = activityCategory(message);
    if (category === null) {
      throw new Error(`Update ${message.type} is not agent activity.`);
    }
    const id = activityStepId(message, category);
    const existing = this.indexes.get(id);
    const index = existing ?? this.sources.length;
    const previous = this.sources[index];
    if (previous !== undefined) {
      this.removeStatus(index);
      const previousCategory = activityCategory(previous);
      if (previousCategory !== category) {
        if (previousCategory !== null) {
          this.removeCategory(previousCategory);
        }
        this.addCategory(category);
      }
    } else {
      this.indexes.set(id, index);
      this.addCategory(category);
    }
    this.sources[index] = message;
    this.addStatus(message, index);
    return index;
  }

  materialize(): AgentActivityStep[] {
    return this.sources.map((_, index) => this.materializeAt(index));
  }

  materializeAt(index: number): AgentActivityStep {
    const message = this.sources[index];
    if (message === undefined) {
      throw new Error(`Activity step ${index} does not exist.`);
    }
    const category = activityCategory(message);
    if (category === null) {
      throw new Error(`Update ${message.type} is not agent activity.`);
    }
    return activityStep(message, category);
  }

  summary(): AgentActivitySummary {
    const failed = this.sourceAt(this.failed.maximum());
    if (failed !== null) {
      const category = requiredActivityCategory(failed);
      return {
        status: "failed",
        summary: `${category} failed: ${activitySubject(failed, category)}`,
        tone: "error",
      };
    }

    const active = this.sourceAt(this.active.maximum());
    if (active !== null) {
      const category = requiredActivityCategory(active);
      const status = activityStatus(active);
      const verb = status === "pending" ? "waiting on" : "running";
      return {
        status,
        summary: `${verb} ${category}: ${activitySubject(active, category)}`,
        tone: "activity",
      };
    }

    return {
      status: null,
      summary: completedSummary(this.sources.length, this.categoryCounts),
      tone: "activity",
    };
  }

  private addStatus(message: AgentPaneUpdate, index: number): void {
    const tone = activityTone(message);
    if (tone === "failed") {
      this.failed.add(index);
    }
    if (tone === "running" || tone === "pending") {
      this.active.add(index);
    }
  }

  private removeStatus(index: number): void {
    this.failed.delete(index);
    this.active.delete(index);
  }

  private addCategory(category: string): void {
    if (category !== "diff") {
      this.categoryCounts.set(category, (this.categoryCounts.get(category) ?? 0) + 1);
    }
  }

  private removeCategory(category: string): void {
    if (category === "diff") {
      return;
    }
    const count = (this.categoryCounts.get(category) ?? 0) - 1;
    if (count === 0) {
      this.categoryCounts.delete(category);
    } else {
      this.categoryCounts.set(category, count);
    }
  }

  private sourceAt(index: number | null): AgentPaneUpdate | null {
    return index === null ? null : (this.sources[index] ?? null);
  }
}

export function isAgentActivity(message: AgentPaneUpdate): boolean {
  return activityCategory(message) !== null;
}

function activityStep(message: AgentPaneUpdate, category: string): AgentActivityStep {
  const summary = activityStepSummary(message);
  const normalized = normalizeText(summary);
  return {
    category,
    detailText: normalizeText(message.text),
    id: activityStepId(message, category),
    label: normalized === null ? category : `${category} ${normalized}`,
    status: activityStatus(message),
    tone: activityTone(message),
  };
}

function activityCategory(message: AgentPaneUpdate): string | null {
  switch (message.type) {
    case "file-patch-updated":
      return "edit";
    case "item-completed":
      return message.itemType === "agentMessage" || message.itemType === "plan"
        ? null
        : activityPrefix(message);
    case "item-started":
    case "command-output-delta":
    case "plan-delta":
      return activityPrefix(message);
    case "turn-diff":
      return "diff";
    default:
      return null;
  }
}

function requiredActivityCategory(message: AgentPaneUpdate): string {
  const category = activityCategory(message);
  if (category === null) {
    throw new Error(`Update ${message.type} is not agent activity.`);
  }
  return category;
}

function activityStepSummary(message: AgentPaneUpdate): string | null | undefined {
  if (message.type === "file-patch-updated") {
    return message.summary;
  }
  if (message.type === "turn-diff") {
    return "ready";
  }
  if (message.type === "plan-delta") {
    return "plan";
  }
  return message.summary;
}

function activityStatus(message: AgentPaneUpdate): string | null {
  switch (message.type) {
    case "file-patch-updated":
      return "updated";
    case "item-completed":
      return normalizeStatus(message.status);
    case "item-started":
    case "command-output-delta":
    case "plan-delta":
      return "running";
    case "turn-diff":
      return "ready";
    default:
      return null;
  }
}

function activityTone(message: AgentPaneUpdate): AgentActivityStep["tone"] {
  if (message.type !== "item-completed") {
    return message.type === "item-started" ||
      message.type === "command-output-delta" ||
      message.type === "plan-delta"
      ? "running"
      : "muted";
  }
  const status = normalizeStatus(message.status);
  if (status === "failed" || status === "error") {
    return "failed";
  }
  if (status === "pending") {
    return "pending";
  }
  return status === "running" ? "running" : "muted";
}

function activityStepId(message: AgentPaneUpdate, category: string): string {
  return hasItemId(message)
    ? message.itemId
    : `${message.type}:${message.turnId ?? "session"}:${category}`;
}

function activitySubject(message: AgentPaneUpdate, category: string): string {
  return normalizeText(activityStepSummary(message)) ?? category;
}

function activityPrefix(message: AgentPaneUpdate): string {
  if (message.category !== null && message.category !== undefined) {
    return message.category;
  }
  switch (message.itemType) {
    case "commandExecution":
      return "command";
    case "dynamicToolCall":
    case "mcpToolCall":
      return "tool";
    case "fileChange":
      return "edit";
    case "webSearch":
      return "search";
    default:
      return "step";
  }
}

function completedSummary(sourceCount: number, counts: ReadonlyMap<string, number>): string | null {
  if (counts.size === 0) {
    return sourceCount > 0 ? "diff ready" : null;
  }
  return Array.from(counts.entries())
    .map(([category, count]) => completedCategorySummary(category, count))
    .join(", ");
}

function completedCategorySummary(category: string, count: number): string {
  switch (category) {
    case "command":
      return `ran ${count} command${count === 1 ? "" : "s"}`;
    case "edit":
      return `edited ${count} file${count === 1 ? "" : "s"}`;
    case "search":
      return `searched ${count} time${count === 1 ? "" : "s"}`;
    case "tool":
      return `used ${count} tool${count === 1 ? "" : "s"}`;
    default:
      return `${count} ${category}${count === 1 ? "" : "s"}`;
  }
}
