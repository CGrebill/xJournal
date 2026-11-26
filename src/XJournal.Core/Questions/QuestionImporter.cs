using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using XJournal.Core.Models;
using XJournal.Core.Storage;

namespace XJournal.Core.Questions;

public enum QuestionChange
{
    /// <summary>A question id not seen before.</summary>
    Added,

    /// <summary>Same wording and settings as the current version.</summary>
    Unchanged,

    /// <summary>Wording or settings differ: a new version will be added; old answers keep the old wording.</summary>
    Changed,
}

public sealed record PlannedQuestion(
    ValidQuestion Question,
    int SortOrder,
    QuestionChange Change,
    bool Unarchives,
    QuestionVersion? Previous);

/// <summary>What an import will do, shown to the user before anything is written.</summary>
public sealed record QuestionImportPlan(
    IReadOnlyList<PlannedQuestion> Questions,
    IReadOnlyList<Question> ToArchive,
    bool ReplacesSet)
{
    /// <summary>Problems that stop the import (e.g. a question changing type); empty if it can go ahead.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    public int AddedCount => Questions.Count(q => q.Change == QuestionChange.Added);
    public int ChangedCount => Questions.Count(q => q.Change == QuestionChange.Changed);
    public int UnchangedCount => Questions.Count(q => q.Change == QuestionChange.Unchanged);
    public int RestoredCount => Questions.Count(q => q.Unarchives);

    public bool HasChanges =>
        ToArchive.Count > 0 || Questions.Any(q => q.Change != QuestionChange.Unchanged || q.Unarchives) || OrderChanged;

    internal bool OrderChanged { get; init; }
}

/// <summary>
/// Imports question files without ever losing history: existing versions are never modified or
/// deleted, changed wording becomes a new version, and questions left out are only archived.
/// </summary>
public sealed class QuestionImporter(JournalDatabase db, QuestionRepository questions, IClock clock)
{
    public QuestionImportPlan Plan(IReadOnlyList<ValidQuestion> incoming, bool archiveMissing = true)
    {
        var existing = questions.GetAll(includeArchived: true).ToDictionary(q => q.Id, StringComparer.Ordinal);
        var planned = new List<PlannedQuestion>();
        var errors = new List<string>();
        var orderChanged = false;
        for (var i = 0; i < incoming.Count; i++)
        {
            var question = incoming[i];
            if (!existing.TryGetValue(question.Id, out var current))
            {
                planned.Add(new PlannedQuestion(question, i, QuestionChange.Added, false, null));
                continue;
            }

            if (current.Current.Type != question.Type)
            {
                errors.Add($"\"{question.Id}\" is a {current.Current.Type.ToKey()} question, but the file makes it a " +
                           $"{question.Type.ToKey()} question. A question can't change type: give it a new id " +
                           "(the old question keeps its answers).");
            }

            var change = ContentHash(question.Text, question.Type, question.Config) == current.Current.ContentHash
                ? QuestionChange.Unchanged
                : QuestionChange.Changed;
            orderChanged |= current.SortOrder != i;
            planned.Add(new PlannedQuestion(question, i, change, current.Archived, current.Current));
        }

        var incomingIds = incoming.Select(q => q.Id).ToHashSet(StringComparer.Ordinal);
        var toArchive = archiveMissing
            ? existing.Values.Where(q => !q.Archived && !incomingIds.Contains(q.Id)).OrderBy(q => q.SortOrder).ToList()
            : [];

        return new QuestionImportPlan(planned, toArchive, archiveMissing)
        {
            OrderChanged = archiveMissing && orderChanged,
            Errors = errors,
        };
    }

    /// <summary>
    /// Applies a plan atomically. When the plan replaces the set, the file's order becomes the display
    /// order; otherwise existing questions keep their place and new ones are added at the end.
    /// </summary>
    public void Apply(QuestionImportPlan plan)
    {
        if (plan.Errors.Count > 0) throw new JournalException(plan.Errors[0]);

        var connection = db.Connection;
        var now = clock.LocalNow();
        using var transaction = connection.BeginTransaction();

        var nextOrder = connection.ExecuteScalar<int>(
            "SELECT coalesce(max(sort_order) + 1, 0) FROM questions", transaction: transaction);

        foreach (var item in plan.Questions)
        {
            var q = item.Question;
            if (item.Change == QuestionChange.Added)
            {
                var sortOrder = plan.ReplacesSet ? item.SortOrder : nextOrder++;
                connection.Execute(
                    "INSERT INTO questions (id, sort_order, archived) VALUES ($id, $sortOrder, 0)",
                    new { id = q.Id, sortOrder }, transaction);
            }
            else if (plan.ReplacesSet)
            {
                connection.Execute("UPDATE questions SET sort_order = $sortOrder WHERE id = $id",
                    new { id = q.Id, sortOrder = item.SortOrder }, transaction);
            }

            var versionId = item.Change == QuestionChange.Unchanged
                ? item.Previous!.Id
                : GetOrAddVersion(q.Id, q.Text, q.Type, q.Config, now, transaction);

            connection.Execute(
                "UPDATE questions SET current_version_id = $versionId, archived = 0 WHERE id = $id",
                new { id = q.Id, versionId }, transaction);
        }

        foreach (var question in plan.ToArchive)
        {
            connection.Execute("UPDATE questions SET archived = 1 WHERE id = $id", new { id = question.Id }, transaction);
        }

        transaction.Commit();
    }

    /// <summary>Exports the active questions in the import format, so they can be edited and re-imported.</summary>
    public QuestionFile Export() => new()
    {
        Questions = questions.GetAll().Select(q => ToFileItem(q.Id, q.Current)).ToList(),
    };

    internal static QuestionFileItem ToFileItem(string id, QuestionVersion v)
    {
        var c = v.Config;
        return new QuestionFileItem
        {
            Id = id,
            Type = v.Type.ToKey(),
            Text = v.Text,
            Labels = v.Type == QuestionType.Rating && !v.Labels.SequenceEqual(QuestionFile.DefaultLabels) ? [.. v.Labels] : null,
            Min = v.Type == QuestionType.Scale && c.Min != 1 ? c.Min : null,
            Max = v.Type == QuestionType.Scale && c.Max != 10 ? c.Max : null,
            Low = c.Low,
            High = c.High,
            Options = c.Options is null ? null : [.. c.Options],
            Multiple = c.Multiple,
            Unit = c.Unit,
        };
    }

    /// <summary>Reuses an identical earlier version (e.g. wording reverted) or adds a new one.</summary>
    internal long GetOrAddVersion(string questionId, string text, QuestionType type, QuestionConfig config,
        DateTimeOffset createdAt, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        var hash = ContentHash(text, type, config);
        var existing = db.Connection.ExecuteScalar<long?>(
            "SELECT id FROM question_versions WHERE question_id = $questionId AND content_hash = $hash",
            new { questionId, hash }, transaction);
        if (existing is { } id) return id;

        return InsertVersion(db.Connection, questionId, text, type, config, createdAt, transaction);
    }

    /// <summary>Adds a question version row (no reuse check).</summary>
    internal static long InsertVersion(Microsoft.Data.Sqlite.SqliteConnection connection, string questionId, string text,
        QuestionType type, QuestionConfig config, DateTimeOffset createdAt, Microsoft.Data.Sqlite.SqliteTransaction transaction) =>
        connection.ExecuteScalar<long>(
            """
            INSERT INTO question_versions (question_id, text, type, config_json, scale_json, content_hash, created_utc, created_offset)
            VALUES ($questionId, $text, $type, $configJson, $scaleJson, $hash, $utc, $offset)
            RETURNING id
            """,
            new
            {
                questionId, text,
                type = type.ToKey(),
                configJson = config.ToJson(),
                scaleJson = JsonSerializer.Serialize(config.Labels ?? []),
                hash = ContentHash(text, type, config),
                utc = DbTime.Utc(createdAt),
                offset = DbTime.Offset(createdAt),
            },
            transaction);

    /// <summary>Identifies a wording + type + settings combination.</summary>
    public static string ContentHash(string text, QuestionType type, QuestionConfig config)
    {
        var normalized = QuestionFileParser.NormalizeText(text);
        // Ratings keep the original (text + labels) form, so versions saved before question types still match.
        var canonical = type == QuestionType.Rating
            ? JsonSerializer.Serialize(new object[] { normalized, (config.Labels ?? []).Select(s => QuestionFileParser.NormalizeText(s)).ToArray() })
            : JsonSerializer.Serialize(new object[] { normalized, type.ToKey(), config.ToJson() });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
