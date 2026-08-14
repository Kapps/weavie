import type { AgentPaneUpdate } from "../bridge";

export function isAgentPaneDelta(message: AgentPaneUpdate): boolean {
  return (
    message.type === "agent-message-delta" ||
    message.type === "thought-message-delta" ||
    message.type === "user-message-delta" ||
    message.type === "plan-delta" ||
    message.type === "command-output-delta"
  );
}
