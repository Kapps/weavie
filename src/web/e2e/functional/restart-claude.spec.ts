import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Guards the `weavie.claude.restart` command (issue #103): "Restart Claude" must be reachable from the
// command palette and, when run, actually relaunch the Claude pane in place — the recovery path for a
// crash-looped Claude the supervisor breaker stopped. We can't easily script the breaker deterministically
// in a parallel suite, so we prove the wiring on a healthy pane: running the command respawns the child
// (the host logs a fresh claude `started` line) — the same TerminalController.Restart() path the breaker
// case recovers through. The regression this pins is in our code (command → handler → Restart), not the model.
test("Restart Claude command relaunches the Claude pane in place", async ({ page, weavie }) => {
  // The claude pane launched once: exactly one start line for the claude terminal.
  const claudeStarts = () => (weavie.log().match(/terminal\[claude\] started/g) ?? []).length;
  await expect.poll(claudeStarts, { timeout: 20_000 }).toBe(1);

  // The command is discoverable in the palette (Category "Claude", title "Restart Claude").
  await runCommand(page, "Restart Claude");

  // Running it respawns the pane: a second claude start line appears (Restart() tears down and relaunches).
  await expect.poll(claudeStarts, { timeout: 20_000 }).toBe(2);
});
