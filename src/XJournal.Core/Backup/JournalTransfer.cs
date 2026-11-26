using System.Globalization;
using System.Text.Json;
using Dapper;
using XJournal.Core.Accounts;
using XJournal.Core.Models;
using XJournal.Core.Questions;
using XJournal.Core.Storage;

namespace XJournal.Core.Backup;

/// <summary>What a backup or export file contains, shown before importing it.</summary>
public sealed record BackupSummary(
    int EntryCount,
    DateTimeOffset? FirstEntry,
    DateTimeOffset? LastEntry,
    int QuestionCount,
    int AnsweredDays);

/// <summary>Exporting, importing and automatic backups of whole journals.</summary>
public sealed class JournalTransfer(AccountStore accounts)
{
    public static string AppVersion =>
        typeof(JournalTransfer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // ---------------------------------------------------------------- export

    /// <summary>
    /// Writes an encrypted backup (<c>.xjbackup</c>). With no <paramref name="password"/> it is
    /// encrypted with the journal's current password.
    /// </summary>
    public static void ExportEncrypted(Journal journal, string path, string? password = null) =>
        journal.Database.BackupTo(path, password);

    /// <summary>Writes the whole journal as readable, unencrypted JSON.</summary>
    public static void ExportJson(Journal journal, string path)
    {
        var export = BuildExport(journal);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
            JsonSerializer.Serialize(stream, export, JournalExport.SerializerOptions);
        File.Move(temp, path, overwrite: true);
    }

    internal static JournalExport BuildExport(Journal journal)
    {
        var entries = journal.Entries.GetAll();
        var revisions = journal.Entries.GetAllRevisions().ToLookup(r => r.EntryId);
        var uidById = entries.ToDictionary(e => e.Id, e => e.Uid);
        var versions = journal.Questions.GetAllVersions();
        var questionByVersion = versions.ToDictionary(v => v.Id, v => v.QuestionId);

        return new JournalExport
        {
            Format = JournalExport.FormatName,
            ExportedAt = journal.Clock.LocalNow(),
            AppVersion = AppVersion,
            Entries = entries.Select(e => new ExportEntry
            {
                Uid = e.Uid, CreatedAt = e.CreatedAt, UpdatedAt = e.UpdatedAt, Summary = e.Summary, Body = e.Body,
                Revisions = revisions.Contains(e.Id)
                    ? revisions[e.Id].Select(r => new ExportRevision
                    {
                        Summary = r.Summary, Body = r.Body, UpdatedAt = r.UpdatedAt, SavedAt = r.SavedAt, Reason = r.Reason,
                    }).ToList()
                    : null,
            }).ToList(),
            Questions = journal.Questions.GetAll(includeArchived: true).Select(q => new ExportQuestion
            {
                Id = q.Id, SortOrder = q.SortOrder, Archived = q.Archived, CurrentVersion = q.Current.Id,
            }).ToList(),
            QuestionVersions = versions.Select(v => new ExportQuestionVersion
            {
                Key = v.Id, QuestionId = v.QuestionId, Text = v.Text, Type = v.Type.ToKey(), Config = v.Config, CreatedAt = v.CreatedAt,
            }).ToList(),
            AnswerSets = journal.Answers.GetAll().Select(s => new ExportAnswerSet
            {
                Day = DbTime.Day(s.Day),
                EntryUid = s.EntryId is { } id ? uidById.GetValueOrDefault(id) : null,
                AnsweredAt = s.AnsweredAt,
                Answers = s.Values.OrderBy(kv => kv.Key).Select(kv => new ExportAnswer
                {
                    QuestionId = questionByVersion[kv.Key], Version = kv.Key, Number = kv.Value.Number, Text = kv.Value.Text,
                }).ToList(),
            }).ToList(),
            Settings = journal.Settings.GetAll().ToDictionary(),
        };
    }

    // ---------------------------------------------------------------- inspect

    /// <summary>Opens an encrypted backup and summarises it. The backup file itself is never modified.</summary>
    /// <exception cref="InvalidPasswordException"/>
    public static BackupSummary InspectBackup(string path, string password) => WithBackupCopy(path, password, Summarize);

    /// <summary>
    /// Runs <paramref name="read"/> against a temporary copy of a backup, so opening it (which may
    /// upgrade its format) never touches the original file. The copy is always deleted.
    /// </summary>
    private static T WithBackupCopy<T>(string path, string password, Func<Journal, T> read)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"xjournal-{Guid.NewGuid():N}.tmp");
        File.Copy(path, temp);
        try
        {
            using var journal = new Journal("", JournalDatabase.Open(temp, password), SystemClock.Instance);
            return read(journal);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public static BackupSummary InspectJson(string path)
    {
        var export = ReadJson(path);
        var created = export.Entries.Select(e => e.CreatedAt).Order().ToList();
        return new BackupSummary(
            export.Entries.Count,
            created.Count > 0 ? created[0] : null,
            created.Count > 0 ? created[^1] : null,
            export.Questions.Count(q => !q.Archived),
            export.AnswerSets.Count);
    }

    internal static BackupSummary Summarize(Journal journal)
    {
        var connection = journal.Database.Connection;
        var (count, first, firstOffset, last, lastOffset) =
            connection.QuerySingle<(int, long?, long?, long?, long?)>(
                """
                SELECT count(*),
                       (SELECT created_utc FROM entries ORDER BY created_utc LIMIT 1),
                       (SELECT created_offset FROM entries ORDER BY created_utc LIMIT 1),
                       (SELECT created_utc FROM entries ORDER BY created_utc DESC LIMIT 1),
                       (SELECT created_offset FROM entries ORDER BY created_utc DESC LIMIT 1)
                FROM entries
                """);
        return new BackupSummary(
            count,
            first is { } f ? DbTime.From(f, firstOffset ?? 0) : null,
            last is { } l ? DbTime.From(l, lastOffset ?? 0) : null,
            journal.Questions.GetAll().Count,
            connection.ExecuteScalar<int>("SELECT count(*) FROM answer_sets"));
    }

    // ---------------------------------------------------------------- import

    /// <summary>
    /// Imports an encrypted backup as <paramref name="username"/>'s journal.
    /// The account's password becomes <paramref name="newPassword"/> if given, otherwise stays the backup's password.
    /// When replacing an existing account, its current journal is kept in <see cref="AccountStore.SafetyBackupsDirectory"/>.
    /// </summary>
    public Journal ImportBackup(string backupPath, string backupPassword, string username, string? newPassword, bool replaceExisting)
    {
        username = PrepareTarget(username, replaceExisting);
        var temp = accounts.NewTempPath();
        Directory.CreateDirectory(accounts.UsersDirectory);
        File.Copy(backupPath, temp);
        try
        {
            using (var database = JournalDatabase.Open(temp, backupPassword))
            {
                var integrity = database.Connection.ExecuteScalar<string>("PRAGMA integrity_check");
                if (integrity != "ok") throw new JournalException($"The backup is damaged ({integrity}).");
                if (newPassword is not null && newPassword != backupPassword) database.ChangePassword(newPassword);
            }
            accounts.Install(temp, username, replaceExisting);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return accounts.SignIn(username, newPassword ?? backupPassword);
    }

    /// <summary>Imports a JSON export as <paramref name="username"/>'s journal, encrypted with <paramref name="password"/>.</summary>
    public Journal ImportJson(string jsonPath, string username, string password, bool replaceExisting)
    {
        var export = ReadJson(jsonPath);
        username = PrepareTarget(username, replaceExisting);
        var temp = accounts.NewTempPath();
        try
        {
            using (var database = JournalDatabase.Create(temp, password))
                WriteExport(database, export);
            accounts.Install(temp, username, replaceExisting);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return accounts.SignIn(username, password);
    }

    private string PrepareTarget(string username, bool replaceExisting)
    {
        username = username.Trim();
        if (AccountStore.ValidateUsername(username) is { } error) throw new AccountException(error);
        var existing = accounts.FindAccount(username);
        if (existing is not null && !replaceExisting) throw new AccountException("An account with that name already exists.");
        return existing ?? username;
    }

    /// <summary>Reads and validates a JSON export.</summary>
    public static JournalExport ReadJson(string path)
    {
        JournalExport? export;
        try
        {
            using var stream = File.OpenRead(path);
            export = JsonSerializer.Deserialize<JournalExport>(stream, JournalExport.SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new JournalException($"This isn't a valid xJournal export: {ex.Message.Split(" Path:")[0]}");
        }
        if (export is null || export.Format != JournalExport.FormatName)
            throw new NotAJournalException("This file isn't an xJournal export.");
        if (export.FormatVersion > JournalExport.CurrentFormatVersion)
            throw new NewerVersionException(export.FormatVersion, JournalExport.CurrentFormatVersion);
        Validate(export);
        return export;
    }

    private static void Validate(JournalExport export)
    {
        var errors = new List<string>();
        var uids = new HashSet<string>();
        foreach (var entry in export.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Uid) || !uids.Add(entry.Uid))
                errors.Add($"Entry from {entry.CreatedAt:g} has a missing or duplicate uid.");
        }

        var questionIds = export.Questions.Select(q => q.Id).ToHashSet();
        if (questionIds.Count != export.Questions.Count) errors.Add("Duplicate question ids.");
        var versions = new Dictionary<long, ExportQuestionVersion>();
        foreach (var version in export.QuestionVersions)
        {
            if (!versions.TryAdd(version.Key, version)) errors.Add($"Duplicate question version key {version.Key}.");
            if (!questionIds.Contains(version.QuestionId)) errors.Add($"Question version {version.Key} refers to unknown question \"{version.QuestionId}\".");
            switch (version.ResolvedType)
            {
                case null:
                    errors.Add($"Question version {version.Key} has an unknown type \"{version.Type}\".");
                    break;
                case QuestionType.Rating when (version.ResolvedConfig.Labels?.Count ?? 0) < QuestionFileParser.MinLabels:
                    errors.Add($"Question version {version.Key} doesn't have its rating labels.");
                    break;
                case QuestionType.Choice when (version.ResolvedConfig.Options?.Count ?? 0) < QuestionFileParser.MinOptions:
                    errors.Add($"Question version {version.Key} doesn't have its options.");
                    break;
            }
        }
        foreach (var question in export.Questions)
        {
            if (!versions.TryGetValue(question.CurrentVersion, out var v) || v.QuestionId != question.Id)
                errors.Add($"Question \"{question.Id}\" has an invalid current version.");
        }

        var days = new HashSet<string>();
        foreach (var set in export.AnswerSets)
        {
            if (!DateOnly.TryParseExact(set.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || !days.Add(set.Day))
                errors.Add($"Answer set has an invalid or duplicate day \"{set.Day}\".");
            if (set.EntryUid is not null && !uids.Contains(set.EntryUid))
                errors.Add($"Answers for {set.Day} refer to a missing entry.");
            foreach (var answer in set.Answers)
            {
                if (!versions.TryGetValue(answer.Version, out var version))
                    errors.Add($"An answer on {set.Day} refers to an unknown question version.");
                else if (version.ResolvedType is not null && AnswerRules.Validate(version.ToVersion(), answer.Answer) is { } problem)
                    errors.Add($"The answer on {set.Day} to \"{version.Text}\" {problem}.");
            }
        }

        if (errors.Count > 0)
        {
            var shown = string.Join("\n", errors.Take(8));
            var more = errors.Count > 8 ? $"\n…and {errors.Count - 8} more problems." : "";
            throw new JournalException($"The export file has problems:\n{shown}{more}");
        }
    }

    private static void WriteExport(JournalDatabase database, JournalExport export)
    {
        var connection = database.Connection;
        using var transaction = connection.BeginTransaction();

        var entryIds = new Dictionary<string, long>();
        foreach (var e in export.Entries)
        {
            entryIds[e.Uid] = connection.ExecuteScalar<long>(
                """
                INSERT INTO entries (uid, created_utc, created_offset, updated_utc, updated_offset, summary, body)
                VALUES ($Uid, $cu, $co, $uu, $uo, $Summary, $Body) RETURNING id
                """,
                new
                {
                    e.Uid, e.Summary, e.Body,
                    cu = DbTime.Utc(e.CreatedAt), co = DbTime.Offset(e.CreatedAt),
                    uu = DbTime.Utc(e.UpdatedAt), uo = DbTime.Offset(e.UpdatedAt),
                }, transaction);
            InsertRevisions(connection, entryIds[e.Uid], e.Revisions, transaction);
        }

        foreach (var q in export.Questions)
        {
            connection.Execute("INSERT INTO questions (id, sort_order, archived) VALUES ($Id, $SortOrder, $Archived)", q, transaction);
        }

        var versionIds = new Dictionary<long, long>();
        foreach (var v in export.QuestionVersions)
        {
            versionIds[v.Key] = QuestionImporter.InsertVersion(
                connection, v.QuestionId, v.Text, v.ResolvedType!.Value, v.ResolvedConfig, v.CreatedAt, transaction);
        }

        foreach (var q in export.Questions)
        {
            connection.Execute("UPDATE questions SET current_version_id = $v WHERE id = $id",
                new { id = q.Id, v = versionIds[q.CurrentVersion] }, transaction);
        }

        foreach (var s in export.AnswerSets)
        {
            var setId = connection.ExecuteScalar<long>(
                "INSERT INTO answer_sets (day, entry_id, answered_utc, answered_offset) VALUES ($Day, $entryId, $utc, $offset) RETURNING id",
                new
                {
                    s.Day,
                    entryId = s.EntryUid is { } uid ? entryIds[uid] : (long?)null,
                    utc = DbTime.Utc(s.AnsweredAt), offset = DbTime.Offset(s.AnsweredAt),
                }, transaction);
            foreach (var a in s.Answers)
                AnswerRepository.Insert(connection, setId, versionIds[a.Version], a.Answer, transaction);
        }

        foreach (var (key, value) in export.Settings)
        {
            connection.Execute("INSERT INTO settings (key, value) VALUES ($key, $value)", new { key, value }, transaction);
        }

        transaction.Commit();
    }

    internal static void InsertRevisions(Microsoft.Data.Sqlite.SqliteConnection connection, long entryId,
        IEnumerable<ExportRevision>? revisions, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        foreach (var r in revisions ?? [])
        {
            connection.Execute(
                """
                INSERT INTO entry_revisions (entry_id, summary, body, updated_utc, updated_offset, saved_utc, saved_offset, reason)
                VALUES ($entryId, $Summary, $Body, $uu, $uo, $su, $so, $Reason)
                """,
                new
                {
                    entryId, r.Summary, r.Body, r.Reason,
                    uu = DbTime.Utc(r.UpdatedAt), uo = DbTime.Offset(r.UpdatedAt),
                    su = DbTime.Utc(r.SavedAt), so = DbTime.Offset(r.SavedAt),
                }, transaction);
        }
    }

    /// <summary>Opens an encrypted backup (via a temporary copy) and reads everything in it.</summary>
    /// <exception cref="InvalidPasswordException"/>
    public static JournalExport ReadBackup(string path, string password) => WithBackupCopy(path, password, BuildExport);

    // ---------------------------------------------------------------- automatic backups

    /// <summary>
    /// Writes an encrypted backup to the configured folder (if any) and removes the oldest
    /// automatic backups beyond the configured count.
    /// </summary>
    /// <returns>The backup's path, or null when automatic backups are off.</returns>
    /// <exception cref="JournalException">The folder couldn't be written.</exception>
    public static string? RunAutoBackup(Journal journal)
    {
        var folder = journal.Settings.AutoBackupFolder;
        if (folder is null) return null;

        var prefix = $"xJournal-{journal.Username}-";
        var path = Path.Combine(folder, $"{prefix}{journal.Clock.LocalNow():yyyyMMdd-HHmmss}{AccountStore.BackupExtension}");
        try
        {
            Directory.CreateDirectory(folder);
            journal.Database.BackupTo(path);

            var old = Directory.EnumerateFiles(folder, prefix + "*" + AccountStore.BackupExtension)
                .Where(p => Path.GetFileName(p).Length == prefix.Length + "yyyyMMdd-HHmmss".Length + AccountStore.BackupExtension.Length)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(journal.Settings.AutoBackupKeep);
            foreach (var file in old) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new JournalException($"Couldn't write the automatic backup to \"{folder}\": {ex.Message}", ex);
        }
        return path;
    }
}
