import { describe, expect, it, vi } from "vitest";
import { InteractionIntent } from "./interaction-intent";

describe("delayed focus permission", () => {
  it.each(["pointerdown", "keydown", "wheel"])("is revoked by newer %s input", (event) => {
    const input = new EventTarget();
    const interaction = new InteractionIntent(input);
    const intent = interaction.begin();
    input.dispatchEvent(new Event(event));
    expect(intent.current()).toBe(false);
    expect(interaction.capture().current()).toBe(true);
    interaction.dispose();
  });

  it("survives the invoking palette's own close and ambient focus recovery", () => {
    const input = new EventTarget();
    const interaction = new InteractionIntent(input);
    const intent = interaction.begin();
    input.dispatchEvent(new Event("focusout"));
    input.dispatchEvent(new Event("focusin"));
    expect(intent.current()).toBe(true);
    interaction.dispose();
  });

  it("revokes old permission on explicit navigation and session changes, even away and back", () => {
    const interaction = new InteractionIntent(new EventTarget());
    const previous = interaction.begin();
    const current = interaction.begin();
    expect(previous.current()).toBe(false);
    expect(current.current()).toBe(true);
    interaction.invalidate();
    interaction.invalidate();
    expect(current.current()).toBe(false);
    interaction.dispose();
  });

  it("releases its listeners and never grants permission after disposal", () => {
    const input = new EventTarget();
    const remove = vi.spyOn(input, "removeEventListener");
    const interaction = new InteractionIntent(input);
    const intent = interaction.capture();
    interaction.dispose();
    expect(remove).toHaveBeenCalledTimes(3);
    expect(intent.current()).toBe(false);
    expect(interaction.begin().current()).toBe(false);
  });
});
