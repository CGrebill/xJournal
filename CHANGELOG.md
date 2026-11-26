# Changelog

## 1.0.1 (2026-11-26)

**Fixed**
- On Windows, the window title was drawn twice in the top-left corner (the system's title on top of xJournal's own), and an unneeded full-screen button appeared next to minimize, maximize and close.

**Documentation**
- Screenshots in the README, plus a security policy, contributing guide, and issue and pull-request templates.

## 1.0.0 (2026-11-26)

The first release of xJournal.

**Writing**
- Sign in, start typing. Entries are created and timestamped automatically (to the millisecond, with your time zone), and saved as you write.
- A recent entry (last 30 minutes, configurable) reopens when you sign in; otherwise a new one starts.
- Optional one-line summary per entry, word count, and previous versions you can restore.

**Finding things**
- Full-text search with highlighted matches, prefix words and "exact phrases", plus date filters.
- Calendar view of the days you wrote and checked in.
- "On this day": what you wrote on this date last month and in earlier years.

**Check-ins and trends**
- Your own 1–5 scale questions, imported from a JSON file and answered once a day.
- Answers are tied to the exact wording they were given for; rewording or removing questions never loses history.
- Trend charts per question, and writing stats: streaks, weekly and monthly totals, words per week.

**Privacy and safety**
- Each journal is a single file encrypted with your password (SQLite3 Multiple Ciphers, ChaCha20-Poly1305).
- Locks itself after 15 minutes of inactivity (configurable). A journal can't be open twice at once.
- Unexpected errors are logged locally (never journal text) and explained instead of crashing.

**Moving and backing up**
- Encrypted backups, automatic backups to any folder (e.g. a cloud-synced one), and import on another computer.
- Merge journals edited on two computers without losing anything.
- Readable exports as JSON or Markdown.

**Platforms**
- Windows installer and portable build, Linux, and macOS (Apple Silicon and Intel).
