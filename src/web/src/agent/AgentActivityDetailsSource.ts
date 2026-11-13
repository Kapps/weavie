import { type Accessor, createSignal, type Setter } from "solid-js";
import type { ProjectedAgentActivity } from "./AgentPaneActivitySummary";
import type { AgentActivityStep } from "./AgentPaneTranscriptTypes";
import { share } from "./AgentTranscriptSnapshot";

export interface ActivityDetailsReader {
  steps: Accessor<AgentActivityStep[]>;
  release(): void;
}

export interface ActivityDetailsSource {
  retain(): ActivityDetailsReader;
  replace(projection: ProjectedAgentActivity): void;
  update(indexes: ReadonlySet<number>): void;
  snapshot(): AgentActivityStep[];
}

/** Materialization belongs to open readers; releasing one never changes transcript membership. */
export function createActivityDetailsSource(
  initial: ProjectedAgentActivity,
): ActivityDetailsSource {
  let projection = initial;
  let demand:
    | {
        readers: number;
        steps: Accessor<AgentActivityStep[]>;
        setSteps: Setter<AgentActivityStep[]>;
      }
    | undefined;
  return {
    retain() {
      if (demand === undefined) {
        const [steps, setSteps] = createSignal(projection.materialize());
        demand = { readers: 0, steps, setSteps };
      }
      const reader = demand;
      reader.readers++;
      return {
        steps: reader.steps,
        release() {
          if (--reader.readers === 0) demand = undefined;
        },
      };
    },
    replace(next) {
      projection = next;
      demand?.setSteps((previous) => share(previous, projection.materialize()));
    },
    update(indexes) {
      demand?.setSteps((previous) => {
        const steps = [...previous];
        for (const index of indexes) steps[index] = projection.materializeAt(index);
        return steps;
      });
    },
    snapshot: () => demand?.steps() ?? [],
  };
}
