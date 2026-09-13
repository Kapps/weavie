"""Regression checks for combining upstream Hunspell dictionaries."""

import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("update_spelling", Path(__file__).with_name("update-spelling.py"))
updater = importlib.util.module_from_spec(spec)
spec.loader.exec_module(updater)


class MergeEnglishTests(unittest.TestCase):
    def test_preserves_distinct_flags_and_morphology_and_is_deterministic(self):
        sources = {
            "US": (b"# US\nSET UTF-8\n\nTRY abc\n", b"3\ncolor/S\nshared/A\nshared/B st:shared\n"),
            "GB": (b"  # GB\n SET UTF-8 \nTRY abc\n", b"3\ncolour/S\nshared/A\nshared/C\n"),
        }
        aff, dic = updater.merge_english(sources)
        self.assertEqual(aff, b"SET UTF-8\nTRY abc\n")
        self.assertEqual(dic, b"5\ncolor/S\ncolour/S\nshared/A\nshared/B st:shared\nshared/C\n")
        self.assertEqual((aff, dic), updater.merge_english(dict(reversed(list(sources.items())))))

    def test_rejects_incompatible_rules(self):
        with self.assertRaisesRegex(ValueError, "affix rules differ"):
            updater.merge_english({
                "US": (b"SET UTF-8\nSFX S Y 1\nSFX S 0 s .\n", b"1\nword/S\n"),
                "GB": (b"SET UTF-8\nSFX S Y 1\nSFX S 0 es .\n", b"1\nword/S\n"),
            })

    def test_rejects_forbidden_words_that_could_override_another_region(self):
        with self.assertRaisesRegex(ValueError, "forbidden words"):
            updater.merge_english({"US": (b"SET UTF-8\nFORBIDDENWORD !\n", b"1\nword/!\n")})

    def test_rejects_invalid_counts_and_empty_sources(self):
        for data in (b"", b"word\n", b"2\nword\n", b"0\n"):
            with self.subTest(data=data), self.assertRaises(ValueError):
                updater.merge_english({"US": (b"SET UTF-8\n", data)})
        with self.assertRaises(ValueError):
            updater.merge_english({})


if __name__ == "__main__":
    unittest.main()
