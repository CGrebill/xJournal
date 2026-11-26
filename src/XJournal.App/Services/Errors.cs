using Microsoft.Data.Sqlite;
using XJournal.Core;

namespace XJournal.App.Services;

public static class Errors
{
    /// <summary>
    /// Failures the user can understand and act on (a file that can't be written, a wrong password,
    /// a damaged backup…), as opposed to programming errors, which go to the crash handler.
    /// </summary>
    public static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JournalException or SqliteException;
}
