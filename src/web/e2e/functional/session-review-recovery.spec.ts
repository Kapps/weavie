import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { activeSessionSlot, createSession, runCommand, showParked } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("opening a parked branch confirms clearing its damaged review from the Sessions dialog", async ({
  page,
  weavie,
}) => {
  const branch = "feature/workflow-notion-spec-brainstorm";
  await createSession(page, { branch, provider: "claude" });
  const slot = await activeSessionSlot(page);
  await runCommand(page, "Unload Session");
  await showParked(page);
  const chip = page.locator(`.session-chip[data-session-slot="${slot}"]`);
  await expect(chip).toHaveClass(/\bunloaded\b/);
  const database = join(weavie.home, ".weavie", "workspaces", slot, "review", "review.db");
  const damaged = "unreadable review database";
  await writeFile(database, damaged);

  await runCommand(page, "Sessions");
  const inbox = page.locator(".session-inbox");
  const group = inbox.getByRole("region", { name: "Open an existing branch" });
  const existing = group.getByRole("combobox", { name: "Existing branch for the session" });
  const open = group.getByRole("button", { name: "Open", exact: true });
  await existing.fill(branch);
  await open.click();
  const confirmation = page.getByRole("dialog", {
    name: "Clear saved review?",
    exact: true,
  });
  await expect(confirmation).toHaveAccessibleDescription(new RegExp(branch));
  await expect(confirmation.getByRole("button", { name: /Cancel/ })).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(confirmation).toBeHidden();
  await expect(existing).toHaveValue(branch);
  await expect(chip).toHaveClass(/\bunloaded\b/);
  expect(await readFile(database, "utf8")).toBe(damaged);

  await open.click();
  await confirmation.getByRole("button", { name: "Clear review and load", exact: true }).click();
  await expect(inbox).toBeHidden();
  await expect(chip).toHaveClass(/\bactive\b/);
  await expect(chip).not.toHaveClass(/\bunloaded\b/);
});
