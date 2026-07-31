import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The healthy-provider path exercises the same command → Restart wiring used to recover after the supervisor's
// crash-loop breaker, with a second terminal[agent] start proving the child was replaced.
test("Restart Agent command relaunches the session's provider in place", async ({
  page,
  weavie,
}) => {
  const agentStarts = () => (weavie.log().match(/terminal\[agent\] started/g) ?? []).length;
  await expect.poll(agentStarts, { timeout: 20_000 }).toBe(1);

  await runCommand(page, "Restart Agent");

  await expect.poll(agentStarts, { timeout: 20_000 }).toBe(2);
});
