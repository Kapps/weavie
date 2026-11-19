// How this device presents agent conversations: the `agentDisplay` device setting.

import { createSignal } from "solid-js";
import type { AgentDisplay } from "../bridge";
import { deviceSetting } from "../device-settings";

declare global {
  interface Window {
    /** Resolved agent display settings injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_AGENT_DISPLAY__?: AgentDisplay;
  }
}

const setting = deviceSetting<AgentDisplay>(
  "agentDisplay",
  "__WEAVIE_AGENT_DISPLAY__",
  window.__WEAVIE_AGENT_DISPLAY__,
  { replyStartOffset: 0.5, thoughtDisplay: "collapsed", progressDisplay: "all" },
);
const [display, setDisplay] = createSignal(setting.current());
setting.onChanged(setDisplay);

/** The agent display settings, reactive. */
export const agentDisplay = display;
