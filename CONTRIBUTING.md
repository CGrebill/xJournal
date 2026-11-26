# Contributing to xJournal

Thanks for your interest! Bug reports, ideas and pull requests are all welcome.

## Reporting bugs and ideas

- **Bugs:** open an issue with the *Bug report* template. Say which version and operating system you use, and how to make the problem happen.
- **Ideas:** open an issue with the *Feature request* template, describing what you'd like to do rather than how to build it.
- **Security problems:** please report them privately (see [SECURITY.md](SECURITY.md)), not as an issue.

**Never attach real journal files, backups, exports or passwords** to an issue. They're personal, and an issue is public. If a file is needed to reproduce a problem, make one with a test account and made-up text.

The error log (`%APPDATA%\xJournal\logs` on Windows, `~/.config/xJournal/logs` on Linux and macOS) contains only technical details, never journal text, so it's safe to attach.

## Building and testing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
```

```bash
dotnet test
```

```bash
dotnet run --project src/XJournal.App
```

- `src/XJournal.Core` contains everything that isn't UI: storage and encryption, entries, search, questions, backup/merge, insights and exports. Put logic here, and cover it with tests in `tests/XJournal.Core.Tests`.
- `src/XJournal.App` is the Avalonia app (MVVM with CommunityToolkit.Mvvm). The UI tests in `tests/XJournal.App.Tests` run headlessly. Set `XJOURNAL_SCREENSHOTS=<folder>` to save a render of each screen.
- To regenerate the README screenshots:

```bash
XJOURNAL_DOCS_SCREENSHOTS=docs/screenshots dotnet test --filter DocsScreenshots
```

## Pull requests

- Keep each pull request focused on one change, and describe what it changes and why.
- Add or update tests for behaviour changes. Bug fixes should come with a test that fails without the fix.
- CI must pass: it builds with **warnings as errors** (the .NET recommended analyzers are on) and runs every test on Windows and Linux.
- Follow the style of the surrounding code. User-facing text should be plain and friendly.
- **Never lose user data.** Changes to storage need a forward-only migration in `Schema.cs`, and must keep existing journals and backups readable.
- Update `README.md` and `CHANGELOG.md` when you change something users will notice.

## License of contributions

xJournal is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE.md). By submitting a contribution, you agree that:

1. it's your own work, or you have the right to submit it, and
2. it's licensed under the same PolyForm Noncommercial terms, and you also grant the project's author (CGrebill) a perpetual, worldwide, royalty-free right to use, modify and relicense it under any other terms, including commercial ones.

Point 2 keeps the project free to change its license or offer commercial licenses in future, without tracking down every contributor.
