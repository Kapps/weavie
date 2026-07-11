import { describe, expect, it } from "vitest";
import { ReliableAgentFrames } from "./reliable-agent-frames";

describe("ReliableAgentFrames", () => {
  it("retains in-flight agent operations for reconnect and removes them on acknowledgement", () => {
    const frames = new ReliableAgentFrames();
    const upload = JSON.stringify({ type: "agent-attachment-upload", slot: "s1", id: "a1" });
    const submit = JSON.stringify({ type: "agent-submit", slot: "s1", id: "t1" });

    expect(frames.track(upload)).toBe(true);
    expect(frames.track(submit)).toBe(true);
    expect(frames.replay()).toEqual([upload, submit]);

    frames.acknowledge(
      JSON.stringify({ type: "agent-attachment-state", slot: "s1", id: "a1", status: "ready" }),
    );
    expect(frames.replay()).toEqual([submit]);
    frames.acknowledge(
      JSON.stringify({ type: "agent-submission-state", slot: "s1", id: "t1", status: "accepted" }),
    );
    expect(frames.replay()).toEqual([]);
  });

  it("does not retain unrelated bridge traffic or legacy submissions without ids", () => {
    const frames = new ReliableAgentFrames();

    expect(frames.track(JSON.stringify({ type: "term-input", slot: "s1" }))).toBe(false);
    expect(frames.track(JSON.stringify({ type: "agent-submit", slot: "s1" }))).toBe(false);
    expect(frames.replay()).toEqual([]);
  });

  it("replaces a pending upload with a reliable removal until the host confirms it", () => {
    const frames = new ReliableAgentFrames();
    const upload = JSON.stringify({ type: "agent-attachment-upload", slot: "s1", id: "a1" });
    const remove = JSON.stringify({ type: "agent-attachment-remove", slot: "s1", id: "a1" });
    frames.track(upload);

    expect(frames.track(remove)).toBe(true);
    expect(frames.replay()).toEqual([remove]);

    frames.acknowledge(
      JSON.stringify({ type: "agent-attachment-state", slot: "s1", id: "a1", status: "ready" }),
    );
    expect(frames.replay()).toEqual([remove]);

    frames.acknowledge(
      JSON.stringify({ type: "agent-attachment-state", slot: "s1", id: "a1", status: "removed" }),
    );
    expect(frames.replay()).toEqual([]);
  });
});
