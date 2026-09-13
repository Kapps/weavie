"""Import pinned upstream dictionaries; --update resolves and pins their latest releases."""

import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path
import re
import tarfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
NOTICE = ROOT / "third-party/spelling"
RESOURCES = ROOT / "src/Weavie.Core/Spelling/Resources"
MANIFEST = NOTICE / "sources.json"


def download(url):
    request = urllib.request.Request(url, headers={"User-Agent": "Weavie-dictionary-import"})
    with urllib.request.urlopen(request) as response:
        return response.read()


def merge_english(sources):
    rules = None
    entries = set()
    for locale, (aff, dic) in sources.items():
        active = [line.strip() for line in aff.decode("utf-8").splitlines()
                  if line.strip() and not line.lstrip().startswith("#")]
        if rules is not None and rules != active:
            raise ValueError(f"Cannot merge {locale}: English affix rules differ")
        if any(line.split()[0] == "FORBIDDENWORD" for line in active):
            raise ValueError(f"Cannot merge {locale}: forbidden words can override another region's accepted words")
        rules = active
        lines = dic.decode("utf-8").splitlines()
        if not lines or not lines[0].isdigit() or int(lines[0]) != len(lines) - 1:
            raise ValueError(f"Invalid dictionary entry count for {locale}")
        entries.update(lines[1:])
    if not rules or not entries:
        raise ValueError("Cannot merge empty English dictionaries")
    return ("\n".join(rules) + "\n").encode(), (f"{len(entries)}\n" + "\n".join(sorted(entries)) + "\n").encode()


def import_dictionaries(update):
    manifest = json.loads(MANIFEST.read_text())
    if update:
        release = json.loads(download("https://api.github.com/repos/en-wl/wordlist/releases/latest"))
        version = release["tag_name"].removeprefix("rel-")
        if not re.fullmatch(r"\d{4}\.\d{2}\.\d{2}", version):
            raise ValueError(f"Unrecognized ESDB release: {version}")
        manifest["english"]["version"] = version
        for package in manifest["technical"]:
            metadata = json.loads(download(f"https://registry.npmjs.org/{package['name']}/latest"))
            package["version"] = metadata["version"]
    outputs = {}

    def verified(url, source):
        data = download(url)
        digest = hashlib.sha256(data).hexdigest()
        if update:
            source["sha256"] = digest
        elif digest != source["sha256"]:
            raise ValueError(f"Checksum mismatch: {url}")
        return data

    english = manifest["english"]
    regional = {}
    for locale, source in english["locales"].items():
        stem = f"en_{locale}-large"
        archive = verified(
            f"https://github.com/en-wl/wordlist/releases/download/rel-{english['version']}/"
            f"hunspell-{stem}-{english['version']}.zip", source)
        with zipfile.ZipFile(io.BytesIO(archive)) as files:
            regional[locale] = (files.read(f"{stem}.aff"), files.read(f"{stem}.dic"))
            outputs[NOTICE / f"English-{locale}-LICENSE.txt"] = files.read(f"README_{stem}.txt")

    outputs[RESOURCES / "en.aff"], outputs[RESOURCES / "en.dic"] = merge_english(regional)

    words = set()
    for package in manifest["technical"]:
        name = package["name"].split("/")[-1]
        archive = verified(
            f"https://registry.npmjs.org/{package['name']}/-/{name}-{package['version']}.tgz", package)
        with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as files:
            outputs[NOTICE / f"{name}-LICENSE.txt"] = files.extractfile("package/LICENSE").read()
            for path in package["files"]:
                data = files.extractfile(f"package/{path}").read()
                if path.endswith(".gz"):
                    data = gzip.decompress(data)
                for line in data.decode("utf-8").splitlines():
                    word = line.strip()
                    if not word or word.startswith("#"):
                        continue
                    if any(marker in word for marker in "*!~+"):
                        raise ValueError(f"Unsupported CSpell directive in {path}: {word}")
                    # Import standalone words, not paths, flags, numeric literals or compound rules.
                    if re.fullmatch(r"[^\W\d_]+(?:['’][^\W\d_]+)*", word):
                        words.add(word)
    if not words:
        raise ValueError("The technical dictionaries contain no standalone words")
    outputs[RESOURCES / "technical.dic"] = (f"{len(words)}\n" + "\n".join(sorted(words)) + "\n").encode()
    outputs[MANIFEST] = (json.dumps(manifest, indent=2) + "\n").encode()
    for path, data in outputs.items():
        path.write_bytes(b"\n".join(line.rstrip() for line in data.splitlines()) + b"\n")
    print(f"Imported US/CA/GB large English and {len(words)} technical words.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--update", action="store_true", help="Resolve latest upstream releases and update checksums")
    import_dictionaries(parser.parse_args().update)
