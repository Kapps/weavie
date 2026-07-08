import type { AgentActivityStep } from "./AgentPaneTranscriptTypes";

export interface AgentActivitySummary {
  status: string | null;
  summary: string | null;
  tone: "activity" | "error";
}

export function summarizeActivity(steps: readonly AgentActivityStep[]): AgentActivitySummary {
  const failed = latestStep(steps, (step) => step.tone === "failed");
  if (failed !== null) {
    return {
      status: "failed",
      summary: `${failed.category} failed: ${stepSubject(failed)}`,
      tone: "error",
    };
  }

  const active = latestStep(steps, (step) => step.tone === "running" || step.tone === "pending");
  if (active !== null) {
    const verb = active.status === "pending" ? "waiting on" : "running";
    return {
      status: active.status,
      summary: `${verb} ${active.category}: ${stepSubject(active)}`,
      tone: "activity",
    };
  }

  return { status: null, summary: completedSummary(steps), tone: "activity" };
}

function stepSubject(step: AgentActivityStep): string {
  const prefix = `${step.category} `;
  return step.label.startsWith(prefix) ? step.label.slice(prefix.length) : step.label;
}

function latestStep(
  steps: readonly AgentActivityStep[],
  predicate: (step: AgentActivityStep) => boolean,
): AgentActivityStep | null {
  for (let i = steps.length - 1; i >= 0; i -= 1) {
    const step = steps[i];
    if (step !== undefined && predicate(step)) {
      return step;
    }
  }

  return null;
}

function completedSummary(steps: readonly AgentActivityStep[]): string | null {
  const counts = new Map<string, number>();
  for (const step of steps) {
    if (step.category !== "diff") {
      counts.set(step.category, (counts.get(step.category) ?? 0) + 1);
    }
  }

  if (counts.size === 0) {
    return steps.length > 0 ? "diff ready" : null;
  }

  return Array.from(counts.entries())
    .map(([category, count]) => `${count} ${category}${count === 1 ? "" : "s"}`)
    .join(", ");
}
