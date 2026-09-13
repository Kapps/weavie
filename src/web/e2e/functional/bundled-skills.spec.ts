import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { configureFakeAcpMode, createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";

for (const embeddedContext of [true, false]) {
  test(`bundled skills are read from shipped files (embeddedContext=${embeddedContext})`, async ({
    page,
    weavie,
  }) => {
    if (!embeddedContext) await configureFakeAcpMode(page, weavie.home, "no-embedded-context");
    const surface = await createAcpSession(page, "bundled-skills");
    const requests = ["report-weavie-bug", "request-weavie-feature"].map(
      (name) => `read-weavie-skill ${name}`,
    );
    for (const request of requests) {
      await submitAcpDraft(surface, request);
      await expect(
        surface.locator(".agent-entry-message.agent-tone-assistant").last(),
      ).toContainText(
        `Deterministic skill-file check completed: ${request.slice("read-weavie-skill ".length)}. Read SKILL.md and 1 reference(s) through ACP. No issue submitted`,
      );
      await expect(surface.locator(".agent-entry-message.agent-tone-user").last()).toContainText(
        request,
      );
    }
    await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
    await expect(surface).toContainText("Never include source code from non-public repositories");
    await expect(surface).toContainText("Treat unknown repository visibility as non-public");
    await expect(surface).toContainText(
      "obtain approval of that concrete content before publishing",
    );

    const records = (
      await readFile(join(weavie.home, ".weavie", "fake-acp-state", "wire-prompts.jsonl"), "utf8")
    )
      .trim()
      .split(/\r?\n/)
      .map((line) => JSON.parse(line));
    expect(records).toHaveLength(2);
    for (const [index, record] of records.entries()) {
      const blocks = record.parameters.prompt;
      expect(blocks[0]).toEqual({ type: "text", text: requests[index] });
      expect(JSON.stringify(blocks)).not.toContain("Help me file a bug report");
      expect(JSON.stringify(blocks)).not.toContain("Never include source code");
      if (index === 0) {
        const context = blocks[1];
        expect(context.type).toBe(embeddedContext ? "resource" : "text");
        expect(context.annotations.audience).toEqual(["assistant"]);
        const catalog = embeddedContext ? context.resource.text : context.text;
        expect(catalog).toContain("## Weavie-only skills");
        expect(catalog).toContain("report-weavie-bug");
        expect(catalog).toContain("request-weavie-feature");
      } else {
        expect(blocks).toHaveLength(1);
      }
    }
  });
}
