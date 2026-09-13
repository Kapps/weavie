Spell checking uses WeCantSpell.Hunspell 7.0.1, unmodified, under the MPL 1.1
option in its tri-license. Its source is available at
https://github.com/aarondandy/WeCantSpell.Hunspell/tree/ab5709d95b2d23541984d22baa0ab2d1e783582f.
See Hunspell-LICENSE.txt and MPL-1.1.txt.

The bundled merged English dictionary comes from the English Speller Database (ESDB, formerly SCOWL):
https://wordlist.aspell.net/dicts/. The importer verifies that the US, Canadian, and British
large dictionaries have matching active affix rules, then deduplicates and sorts complete entry
lines into en.dic and writes the shared rules to en.aff. Distinct flags and morphology are
preserved. Rule mismatches, forbidden-word rules, and invalid counts fail the import.
Only the merged output is checked in; builds and app startup do not run the importer or merge.
Imports normalize line endings and trailing whitespace. See English-*-LICENSE.txt.

The generated technical.dic contains standalone words from Street Side Software's CSpell
software-terms, TypeScript/JavaScript, C#, Node.js, and Git dictionaries:
https://github.com/streetsidesoftware/cspell-dicts. See dict-*-LICENSE.txt (MIT).
Mixed and regional English selections accept this technical vocabulary case-insensitively. Importing excludes
comments, numeric literals, paths, and punctuation-bearing tokens; it does not import CSpell's
compound patterns, correction directives, or runtime configuration.

sources.json pins every bundled source's version, archive SHA-256, and selected CSpell files.
Run `python3 tools/update-spelling.py` from the repository to reproduce the bundled assets.
Run `python3 tools/update-spelling.py --update` to import and pin the latest upstream releases;
review the generated diff and run the spelling tests before opening an update PR.
Run `python3 -m unittest discover -s tools -p test_update_spelling.py` to test the merge validation. Vocabulary
corrections belong upstream, never in hand-edited copies of these resources.

Additional dictionaries are downloaded on request from wooorm/dictionaries at
commit 8cfea406b505e4d7df52d5a19bce525df98c54ab:
https://github.com/wooorm/dictionaries/tree/8cfea406b505e4d7df52d5a19bce525df98c54ab/dictionaries.
Each downloaded dictionary is cached together with its original `license` file.
