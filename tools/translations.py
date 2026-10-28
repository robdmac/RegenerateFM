#!/usr/bin/env python3
"""List and check RegenerateFM's translatable text.

    python3 tools/translations.py keys              # every English key, one JSON list
    python3 tools/translations.py check             # each language's missing, stale and broken entries
    python3 tools/translations.py missing german    # the keys german.json lacks, as a JSON object to fill in

The keys are the English texts passed to Text.T("...") in src/ (the first
argument, a single plain C# string literal). Each language's translations are
src/RegenerateFM.Core/Translations/<language>.json, one object from English to
the language's text, keeping the English's {0}-style placeholders.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCES = ROOT / "src"
TRANSLATIONS = SOURCES / "RegenerateFM.Core" / "Translations"

# FM's interface languages other than English: FM26's 20 less "english" and
# "english (us)", which cover FM24's too.
LANGUAGES = [
    "arabic", "chinese", "danish", "dutch", "french", "german", "greek", "italian", "japanese", "korean",
    "norwegian", "polish", "portuguese", "russian", "spanish", "spanish (latam)", "swedish", "turkish",
]

CALL = re.compile(r'\bText\.T\(\s*"((?:[^"\\\n]|\\.)*)"\s*[,)]')
PLACEHOLDER = re.compile(r"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})")
ESCAPES = {"n": "\n", "t": "\t", "r": "\r", "0": "\0", '"': '"', "'": "'", "\\": "\\"}


def unescape(literal: str) -> str:
    def replace(match: re.Match[str]) -> str:
        code = match.group(1)
        if code[0] == "u":
            return chr(int(code[1:], 16))
        return ESCAPES[code]

    return re.sub(r"\\(u[0-9A-Fa-f]{4}|.)", replace, literal)


def keys() -> list[str]:
    found: set[str] = set()
    for path in sorted(SOURCES.rglob("*.cs")):
        if "/obj/" in path.as_posix() or "/bin/" in path.as_posix():
            continue
        for match in CALL.finditer(path.read_text(encoding="utf-8")):
            found.add(unescape(match.group(1)))
    return sorted(found)


def file_name(language: str) -> str:
    return language.replace(" (", "-").replace(")", "").replace(" ", "-") + ".json"


def load(language: str) -> dict[str, str]:
    path = TRANSLATIONS / file_name(language)
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}


def placeholders(text: str) -> list[str]:
    return sorted(PLACEHOLDER.findall(text))


def problems(language: str, wanted: list[str]) -> list[str]:
    table = load(language)
    found = []
    for key in wanted:
        if key not in table:
            found.append(f"missing: {key!r}")
        elif placeholders(table[key]) != placeholders(key):
            found.append(f"placeholders differ: {key!r} -> {table[key]!r}")
    for key in table:
        if key not in wanted:
            found.append(f"stale: {key!r}")
    return found


def main() -> int:
    command = sys.argv[1] if len(sys.argv) > 1 else "check"
    wanted = keys()
    if command == "keys":
        print(json.dumps(wanted, ensure_ascii=False, indent=1))
        return 0
    if command == "missing":
        table = load(sys.argv[2])
        print(json.dumps({key: "" for key in wanted if key not in table}, ensure_ascii=False, indent=1))
        return 0
    failed = False
    for language in LANGUAGES:
        found = problems(language, wanted)
        print(f"{language}: {len(wanted) - sum(f.startswith('missing') for f in found)}/{len(wanted)}"
              + (f", {len(found)} problems" if found else ""))
        for problem in found[:10]:
            print("   ", problem)
        failed |= bool(found)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
