from pathlib import Path
import importlib.util
import unittest


spec = importlib.util.spec_from_file_location("plan", Path(__file__).with_name("plan_e2e_shards.py"))
planner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(planner)


def report(*tests):
    """A Playwright JSON report of (project, file, seconds) tests, nested the way describes nest them."""
    return {"suites": [{"specs": [], "suites": [{"specs": [
        {"file": file, "tests": [{"projectName": project, "results": [{"duration": seconds * 1000}]}]}
        for project, file, seconds in tests]}]}]}


class PlanE2eShardsTests(unittest.TestCase):
    def test_units_group_tests_by_project_and_file(self):
        found = planner.units(report(("headless", "a.spec.ts", 2), ("headless", "a.spec.ts", 3),
                                     ("remote", "a.spec.ts", 1)))
        self.assertEqual({"[headless] › a.spec.ts": [2, 5.0], "[remote] › a.spec.ts": [1, 1.0]}, found)

    def test_plan_balances_by_recorded_time_and_covers_every_unit_once(self):
        listed = {f"[headless] › {name}.spec.ts": [1, 0.0] for name in "abcde"}
        timings = {"[headless] › a.spec.ts": 9, "[headless] › b.spec.ts": 5, "[headless] › c.spec.ts": 4,
                   "[headless] › d.spec.ts": 3, "[headless] › e.spec.ts": 2}
        shards, loads = planner.plan(listed, timings, 2)
        self.assertEqual(sorted(listed), sorted(u for shard in shards for u in shard))
        self.assertEqual([12, 11], sorted(loads, reverse=True))

    def test_unrecorded_files_weigh_their_tests_at_the_median_rate(self):
        listed = {"[headless] › old.spec.ts": [2, 0.0], "[headless] › new.spec.ts": [4, 0.0]}
        _, loads = planner.plan(listed, {"[headless] › old.spec.ts": 6}, 2)
        self.assertEqual([12, 6], sorted(loads, reverse=True))

    def test_more_shards_than_files_fails(self):
        with self.assertRaises(SystemExit):
            planner.plan({"[headless] › a.spec.ts": [1, 0.0]}, {}, 2)


if __name__ == "__main__":
    unittest.main()
