# Security policy

xJournal's main promise is that your journal stays private, so security reports are very welcome.

## Supported versions

| Version | Supported |
| --- | --- |
| 1.0.x (latest) | Yes |

Fixes go into the latest release. Please update before reporting.

## Reporting a vulnerability

**Please don't open a public issue for security problems.** Report them privately instead:

- Use **[Report a vulnerability](https://github.com/CGrebill/xJournal/security/advisories/new)** on this repository's Security tab (GitHub private vulnerability reporting).

Please include:

- the xJournal version and operating system,
- what an attacker could do (e.g. read a journal without the password, recover deleted text, crash the app with a crafted file),
- steps or a minimal file to reproduce it, made with **test data only**.

**Never send a real journal (`.xjdb`), backup (`.xjbackup`), export, or password.** Make a fresh account with throwaway content instead.

You can expect an acknowledgement within a week. Once a fix is released, the advisory is published with credit to you, unless you'd rather stay anonymous.

## Scope

In scope:

- **Journal encryption:** the database, search index and backups are encrypted with SQLite3 Multiple Ciphers (ChaCha20-Poly1305), with a key derived from the password (PBKDF2, 256,000 iterations).
- **Handling of untrusted files:** backups, JSON exports and question files being imported or merged.
- **Data leaks:** journal text ending up anywhere unencrypted without the user asking for it (logs, temp files, crash reports).

Expected behaviour, not vulnerabilities:

- **Weak passwords:** anyone with your journal file can try to guess the password offline. The slow key derivation makes each guess expensive, but a short or common password can still be guessed.
- **Unencrypted exports:** the JSON and Markdown exports are readable by design. The app warns before writing them.
- **Access to an unlocked session:** someone at your computer while you're signed in can read your journal. Auto-lock (Settings → Security) limits this.
- **Unsigned downloads:** the release downloads aren't code-signed yet. Check them against `SHA256SUMS.txt` on the release.
