# xJournal

A private desktop journal that runs itself. Sign in and start typing. Entries are created, timestamped and saved automatically, and you can find any of them later by date or by what you wrote. Daily 1–5 check-in questions track how things change over time.

Built with .NET 10 and [Avalonia](https://avaloniaui.net/) + [FluentAvalonia](https://github.com/amwx/FluentAvalonia), so it has a Windows 11 (WinUI-style) look with Mica, and it runs on Windows, macOS and Linux.

## Features

- **Automatic entries.** Signing in reopens your last entry if you edited it in the last 30 minutes (configurable). Otherwise a new entry starts. An entry is stamped with the exact moment you start writing (date, time to the millisecond, and your UTC offset). It's saved 1.5 s after you stop typing, and also when you switch entries, leave the window, sign out or close the app. Empty entries are never saved.
- **Summary line.** An optional one-line tagline per entry. It's searchable.
- **Search.** Full-text search across all entries. Words match as prefixes, and `"quoted phrases"` match exactly. Matches are highlighted. You can filter by date (today, past 7/30 days, past year, or a custom range).
- **Check-in questions.** You import your own 1–5 scale questions from a JSON file (format below) and answer them in a separate window, once per day. Answers save as you click, and each day's answers are linked to the entry you were writing. A gentle reminder appears if you haven't answered yet today.
- **Trends.** A chart of each question over time (30 days, 90 days, a year, or all time). Hovering shows the exact wording that was answered, and clicking a point opens that day's entry.
- **Encrypted.** Your password encrypts the whole journal file (see Security).
- **Backup and moving computers.** You can export an encrypted backup or a readable JSON copy, then import either from the sign-in screen on another computer. Optional automatic backups go to any folder (e.g. a OneDrive, Dropbox or Google Drive folder) every time you sign out.
- **Writing on more than one computer.** Merge a backup from another computer into your journal (see below).
- **Previous versions.** When a merge replaces an entry's text, the old text is kept. You can restore it from the editor's **…** menu.

## Keyboard shortcuts

| Keys | Action |
| --- | --- |
| Ctrl+N | New entry |
| Ctrl+F | Search entries |
| Ctrl+Q | Open today's check-in |
| Ctrl+S | Save now (it also saves on its own) |
| Esc | Close search, or close the check-in window |

## Building and running

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/XJournal.App
```

```bash
dotnet test
```

To publish a self-contained Windows build:

```bash
dotnet publish src/XJournal.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The UI tests run headlessly. Set `XJOURNAL_SCREENSHOTS=<folder>` to save a render of each screen.

The app icon is drawn by [`tools/make_icon.py`](tools/make_icon.py), which needs Pillow. Run `python3 tools/make_icon.py` to regenerate `xjournal.ico` and `xjournal.png`.

## Writing on more than one computer

To keep one journal on two computers:

1. On the laptop, open **Settings → Export encrypted backup…**.
2. On the home computer, open **Settings → Merge in a journal…** and pick that file.
3. To sync the other way, export from home and merge it on the laptop.

How a merge treats your data:

- **It only adds.** Nothing is ever deleted. An entry you deleted on one computer comes back if the other computer still has it.
- **Entries are matched by an internal id**, so the same entry is never duplicated. If an entry was edited on both computers, the most recently edited text wins and the other text is kept as a *previous version*. Restore it any time from **… → Previous versions**.
- **Check-in answers** are combined per day and per question. Where both computers answered the same question on the same day, the more recently saved answer wins.
- **Questions** are matched by `id`, and their wordings by content, so every answer keeps the exact wording it was given for. The newest wording becomes the current one.
- **A safety copy of your journal is saved first**, in the `replaced` folder (see below). The dialog shows exactly what will change before anything is written, and merging the same file twice does nothing.

## Check-in question files

Questions are plain JSON, so you can write them in any editor. Comments and trailing commas are allowed. To start from a sample, use **Questions → Save a sample question file…** in the check-in window ([`sample-questions.json`](src/XJournal.App/Assets/sample-questions.json)).

```jsonc
{
  "version": 1,
  // Labels for 1..5, lowest first. Optional; this is the default.
  "scale": ["Entirely disagree", "Somewhat disagree", "Neither agree nor disagree",
            "Somewhat agree", "Entirely agree"],
  "questions": [
    { "id": "energy", "text": "I had plenty of energy today." },
    { "id": "stress", "text": "How stressed did I feel?",
      "scale": ["Not at all", "A little", "Moderately", "Very", "Extremely"] }
  ]
}
```

**Your answers can't be lost by re-importing:**

- A question is identified by its `id`. Keep the id the same and you can change the text or scale freely. The change becomes a new *version*, and earlier answers stay attached to the exact wording they were given for. Trends show where the wording changed.
- Question versions can never be edited or deleted. This is enforced inside the database itself, not just by the app.
- A question you leave out of a later file is **archived**, not deleted. It disappears from the check-in, but its answers and trends remain. Put it back in a file and it returns. You can also import in "add only" mode, which archives nothing.
- Before anything is saved, the importer shows exactly what will be added, reworded and archived.

## Where your data lives

| What | Location |
| --- | --- |
| Journals (one encrypted file per account) | `%APPDATA%\xJournal\users\<username>.xjdb` on Windows, `~/.config/xJournal/users/` on Linux and macOS |
| Copies kept when an import replaces an account, and safety copies taken before a merge | `…\xJournal\replaced\` |
| Last username (not sensitive) | `…\xJournal\state.json` |

Each journal is a single SQLite database. Entries, the search index, questions, answers and settings are all inside it.

## Security

- The whole database file is encrypted with **SQLite3 Multiple Ciphers** (ChaCha20-Poly1305). The key is derived from your password with PBKDF2 (256,000 iterations). That covers the entries, the full-text search index and the temporary journal files.
- Your password (or anything derived from it) isn't stored anywhere. Signing in simply tries to decrypt the file.
- **If you forget your password, the journal can't be recovered.** Keep a backup, and keep its password somewhere safe.
- An encrypted backup (`.xjbackup`) opens with your password at the time of the backup, or with a separate password you choose when exporting.
- The readable JSON export is **not** encrypted. The app warns you before writing one.

## Project layout

```
src/XJournal.Core     storage, encryption, entries, search, questions, backups (no UI)
src/XJournal.App      Avalonia desktop app (views, view models, theme)
tests/                xUnit tests for the core and headless UI tests for the app
```
