import { afterEach, expect, it, vi } from "vitest";
import { parseEnvelope } from "./message-envelope";

afterEach(() => {
  vi.unstubAllGlobals();
});

it("uses a fresh page epoch without requiring secure-context crypto", async () => {
  vi.stubGlobal("crypto", {});
  vi.spyOn(Math, "random").mockReturnValueOnce(0.123456).mockReturnValueOnce(0.654321);

  vi.resetModules();
  const firstModule = await import("./message-bus");
  let firstJson = "";
  const first = new firstModule.MessageBus("session", { slot: "a", incarnation: "a1" }, (json) => {
    firstJson = json;
  });
  const firstRequest = first.feature("dummy").request("read", {});
  const firstId = parseEnvelope(firstJson)?.requestId;
  first.close("reload");
  await expect(firstRequest).rejects.toThrow("reload");

  vi.resetModules();
  const secondModule = await import("./message-bus");
  let secondJson = "";
  const second = new secondModule.MessageBus(
    "session",
    { slot: "a", incarnation: "a1" },
    (json) => {
      secondJson = json;
    },
  );
  const secondRequest = second.feature("dummy").request("read", {});
  const secondId = parseEnvelope(secondJson)?.requestId;
  second.close("done");
  await expect(secondRequest).rejects.toThrow("done");

  expect(firstId).toMatch(/^s-[a-z0-9]{8}-1-1$/);
  expect(secondId).toMatch(/^s-[a-z0-9]{8}-1-1$/);
  expect(secondId).not.toBe(firstId);
});
