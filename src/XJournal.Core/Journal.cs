using XJournal.Core.Questions;
using XJournal.Core.Storage;

namespace XJournal.Core;

/// <summary>A signed-in user's open journal and everything that operates on it.</summary>
public sealed class Journal : IDisposable
{
    internal Journal(string username, JournalDatabase database, IClock clock, string? safetyBackupsDirectory = null)
    {
        Username = username;
        SafetyBackupsDirectory = safetyBackupsDirectory;
        Database = database;
        Clock = clock;
        Entries = new EntryRepository(database, clock);
        Questions = new QuestionRepository(database);
        Answers = new AnswerRepository(database);
        Settings = new SettingsRepository(database);
        QuestionImporter = new QuestionImporter(database, Questions, clock);
    }

    public string Username { get; }
    public JournalDatabase Database { get; }
    public IClock Clock { get; }
    public EntryRepository Entries { get; }
    public QuestionRepository Questions { get; }
    public AnswerRepository Answers { get; }
    public SettingsRepository Settings { get; }
    public QuestionImporter QuestionImporter { get; }

    /// <summary>Where automatic safety copies go before risky operations (e.g. merging), if known.</summary>
    public string? SafetyBackupsDirectory { get; }

    public void Dispose() => Database.Dispose();
}
