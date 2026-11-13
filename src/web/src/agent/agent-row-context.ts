import { createContext } from "solid-js";

/** The transcript row an entry renders in: what stays mounted for it, and how to scroll to another entry. */
export interface AgentRow {
  readonly rowId: string;
  reveal(entryId: string): void;
}

/** Absent outside the agent pane's transcript, such as in an editor tab. */
export const AgentRowContext = createContext<AgentRow>();

/** The toggle of the card an entry renders inside, for a lone block's Alt+B. */
export const AgentCardToggleContext = createContext<() => void>();
