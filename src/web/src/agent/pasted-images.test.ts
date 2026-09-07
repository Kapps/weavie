import { describe, expect, it } from "vitest";
import { agentImageBlob, encodeAgentImage, MAX_AGENT_IMAGE_BYTES } from "./pasted-images";

describe("agent image preparation", () => {
  it.each([
    "image/png",
    "image/jpeg",
    "image/gif",
    "image/webp",
  ])("preserves supported %s bytes and MIME without decoding the pixels", async (mime) => {
    const image = await encodeAgentImage(agentImageBlob(mime, "AQIDBA=="));
    expect(image).toEqual({ mime, dataB64: "AQIDBA==" });
  });

  it("preserves an image just below the host's padded base64 limit", async () => {
    const bytes = new Uint8Array(MAX_AGENT_IMAGE_BYTES - 2).fill(123);
    const image = await encodeAgentImage(new Blob([bytes], { type: "image/png" }));
    expect(Buffer.from(image.dataB64, "base64").equals(bytes)).toBe(true);
  });

  it("rejects empty and unsupported images with user-facing errors", async () => {
    await expect(encodeAgentImage(new Blob([], { type: "image/png" }))).rejects.toThrow(
      "The pasted image was empty.",
    );
    await expect(encodeAgentImage(new Blob(["svg"], { type: "image/svg+xml" }))).rejects.toThrow(
      "use PNG, JPEG, GIF, or WebP",
    );
  });
});
