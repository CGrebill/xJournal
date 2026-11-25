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

    /// <summary>Same wording and scale as the current version.</summary>
    Unchanged,

    /// <summary>Wording or scale differs: a new version will be added; old answers keep the old wording.</summary>
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
        var orderChanged = false;
        for (var i = 0; i < incoming.Count; i++)
        {
            var question = incoming[i];
            if (!existing.TryGetValue(question.Id, out var current))
            {
                planned.Add(new PlannedQuestion(question, i, QuestionChange.Added, false, null));
                continue;
            }

            var change = ContentHash(question.Text, question.Scale) == current.Current.ContentHash
                ? QuestionChange.Unchanged
                : QuestionChange.Changed;
            orderChanged |= current.SortOrder != i;
            planned.Add(new PlannedQuestion(question, i, change, current.Archived, current.Current));
        }

        var incomingIds = incoming.Select(q => q.Id).ToHashSet(StringComparer.Ordinal);
        var toArchive = archiveMissing
            ? existing.Values.Where(q => !q.Archived && !incomingIds.Contains(q.Id)).OrderBy(q => q.SortOrder).ToList()
            : [];

        return new QuestionImportPlan(planned, toArchive, archiveMissing) { OrderChanged = archiveMissing && orderChanged };
    }

    /// <summary>
    /// Applies a plan atomically. When the plan replaces the set, the file's order becomes the display
    /// order; otherwise existing questions keep their place and new ones are added at the end.
    /// </summary>
    public void Apply(QuestionImportPlan plan)
    {
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
                : GetOrAddVersion(q.Id, q.Text, q.Scale, now, transaction);

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
    public QuestionFile Export()
    {
        var active = questions.GetAll();
        return new QuestionFile
        {
            Scale = [.. QuestionFile.DefaultScale],
            Questions = active.Select(q => new QuestionFileItem
            {
                Id = q.Id,
                Text = q.Current.Text,
                Scale = q.Current.Scale.SequenceEqual(QuestionFile.DefaultScale) ? null : [.. q.Current.Scale],
            }).ToList(),
        };
    }

    /// <summary>Reuses an identical earlier version (e.g. wording reverted) or adds a new one.</summary>
    internal long GetOrAddVersion(string questionId, string text, IReadOnlyList<string> scale, DateTimeOffset createdAt,
        Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        var hash = ContentHash(text, scale);
        var existing = db.Connection.ExecuteScalar<long?>(
            "SELECT id FROM question_versions WHERE question_id = $questionId AND content_hash = $hash",
            new { questionId, hash }, transaction);
        if (existing is { } id) return id;

        return db.Connection.ExecuteScalar<long>(
            """
            INSERT INTO question_versions (question_id, text, scale_json, content_hash, created_utc, created_offset)
            VALUES ($questionId, $text, $scaleJson, $hash, $utc, $offset)
            RETURNING id
            """,
            new
            {
                questionId, text, hash,
                scaleJson = JsonSerializer.Serialize(scale),
                utc = DbTime.Utc(createdAt),
                offset = DbTime.Offset(createdAt),
            },
            transaction);
    }

    /// <summary>Identifies a wording + scale combination.</summary>
    public static string ContentHash(string text, IReadOnlyList<string> scale)
    {
        var canonical = JsonSerializer.Serialize(new object[] { QuestionFileParser.NormalizeText(text), scale.Select(s => QuestionFileParser.NormalizeText(s)).ToArray() });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
