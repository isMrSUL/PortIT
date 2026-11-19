"""Refuse to publish anything that looks like a secret.

  python tools/secret_scan.py            the files that would be committed now (tracked + new)
  python tools/secret_scan.py --history  every version of every file ever pushed (git history)

Exit code 1 when something is found. Run by tools/make_release.ps1 before every release (2026-11-19: "don't leak
any API or password of mine, or anything about Cloudflare, in PortIT's GitHub source").

What counts: Discord webhooks, GitHub / Cloudflare / cloud API tokens, private keys, 'password = ...' style lines,
any Windows user folder, a Cloudflare Worker address,
and the owner's private patterns (kept outside the repo). Things that are public ON PURPOSE are listed in ALLOWED.
"""
import os
import re
import subprocess
import sys

PATTERNS = [
    ("Discord webhook", r"discord(?:app)?\.com/api/webhooks/\d+/[\w-]+"),
    ("GitHub token", r"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}\b|\bgithub_pat_[A-Za-z0-9_]{40,}\b"),
    ("Cloudflare API token", r"(?i)(?:cf|cloudflare)[_-]?(?:api)?[_-]?(?:token|key)\s*[:=]\s*['\"]?[A-Za-z0-9_-]{30,}"),
    ("bearer token", r"(?i)\bbearer\s+[A-Za-z0-9._-]{30,}"),
    ("AWS key", r"\bAKIA[0-9A-Z]{16}\b"),
    ("Google API key", r"\bAIza[0-9A-Za-z_-]{35}\b"),
    ("private key", r"-----BEGIN (?:RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----"),
    ("password / secret / token in code", r"(?i)\b(?:password|passwd|pwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token)\b\s*[:=]\s*['\"][^'\"\s]{8,}['\"]"),
    ("a Windows user folder", r"(?i)\b[A-Z]:[\\/]+Users[\\/]+(?!Public\b|Default\b)[^\\/\s\"']+[\\/]"),
    ("a Cloudflare Worker address", r"(?i)\b[\w-]+\.workers\.dev\b"),
]

# Personal things to look for (an e-mail, a name, a folder) are NOT written here - that would publish them. They are
# read from a file outside the repo, one regular expression a line:  %USERPROFILE%\.secret_scan_private.txt
_private = os.path.join(os.path.expanduser("~"), ".secret_scan_private.txt")
if os.path.exists(_private):
    for _line in open(_private, encoding="utf-8"):
        _line = _line.strip()
        if _line and not _line.startswith("#"):
            PATTERNS.append(("a private pattern", _line))

# public on purpose: the relay key is documented as "not a secret" (it only stops random web requests). The relay's
# ADDRESS is not in the source at all (app/relay.local.txt, baked in at build time), and the webhooks live on the relay.
ALLOWED = [
    r"portit-report-v1",
    # examples and placeholders, not anyone's real folder (the Report window replaces a user's folder with NAME)
    r"(?i)Users[\\/]+(?:NAME|yourname|your_name|you|user|username|<[^>]*>)[\\/]",
]

SKIP = re.compile(r"(?i)\.(png|jpg|jpeg|gif|ico|dds|zip|dll|exe|pdb|bin|pac|ttf|otf|woff2?)$")


def git(*a):
    return subprocess.run(["git", *a], capture_output=True, text=True, encoding="utf-8", errors="replace").stdout


def scan_text(where, text, hits):
    for line_no, line in enumerate(text.splitlines(), 1):
        for name, pat in PATTERNS:
            for m in re.finditer(pat, line):
                if any(re.search(a, m.group(0)) for a in ALLOWED):
                    continue
                hits.append((where, line_no, name, m.group(0)[:60]))


def main():
    hits = []
    if "--history" in sys.argv:
        # every blob that ever existed in any commit
        seen = set()
        for line in git("rev-list", "--all", "--objects").splitlines():
            parts = line.split(" ", 1)
            if len(parts) != 2 or SKIP.search(parts[1]) or parts[0] in seen:
                continue
            seen.add(parts[0])
            if git("cat-file", "-t", parts[0]).strip() != "blob":
                continue
            scan_text(f"history:{parts[1]}", git("cat-file", "-p", parts[0]), hits)
    else:
        files = set(git("ls-files").splitlines()) | set(git("ls-files", "--others", "--exclude-standard").splitlines())
        for f in sorted(files):
            if SKIP.search(f):
                continue
            try:
                scan_text(f, open(f, encoding="utf-8", errors="replace").read(), hits)
            except OSError:
                pass
    for where, n, name, what in hits:
        print(f"FOUND  {name:34} {where}:{n}   {what}")
    print(f"{len(hits)} problem(s)" if hits else "clean - nothing that looks like a secret")
    return 1 if hits else 0


if __name__ == "__main__":
    sys.exit(main())
