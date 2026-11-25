using System.Text.Json;
using Dapper;
using XJournal.Core.Models;

namespace XJournal.Core.Storage;

public sealed class QuestionRepository(JournalDatabase db)
{
    private const string VersionColumns =
        "v.id, v.question_id, v.text, v.scale_json, v.content_hash, v.created_utc, v.created_offset";

    /// <summary>Questions in display order. Archived questions are excluded unless requested.</summary>
    public IReadOnlyList<Question> GetAll(bool includeArchived = false) =>
        db.Connection.Query<QuestionRow>(
                $"""
                 SELECT q.sort_order, q.archived, {VersionColumns}
                 FROM questions q JOIN question_versions v ON v.id = q.current_version_id
                 WHERE $includeArchived OR q.archived = 0
                 ORDER BY q.archived, q.sort_order, q.id
                 """, new { includeArchived })
            .Select(r => new Question(r.QuestionId, (int)r.SortOrder, r.Archived != 0, r.ToVersion()))
            .ToList();

    public Question? Get(string id) => GetAll(includeArchived: true).FirstOrDefault(q => q.Id == id);

    /// <summary>Every version of a question, oldest first.</summary>
    public IReadOnlyList<QuestionVersion> GetVersions(string questionId) =>
        db.Connection.Query<VersionRow>(
                $"SELECT {VersionColumns} FROM question_versions v WHERE v.question_id = $questionId ORDER BY v.id",
                new { questionId })
            .Select(r => r.ToVersion()).ToList();

    public IReadOnlyList<QuestionVersion> GetAllVersions() =>
        db.Connection.Query<VersionRow>($"SELECT {VersionColumns} FROM question_versions v ORDER BY v.id")
            .Select(r => r.ToVersion()).ToList();

    public QuestionVersion? GetVersion(long id) =>
        db.Connection.QuerySingleOrDefault<VersionRow>(
            $"SELECT {VersionColumns} FROM question_versions v WHERE v.id = $id", new { id })?.ToVersion();

    public bool Any() => db.Connection.ExecuteScalar<bool>("SELECT EXISTS (SELECT 1 FROM questions WHERE archived = 0)");

    private class VersionRow
    {
        public long Id { get; set; }
        public string QuestionId { get; set; } = "";
        public string Text { get; set; } = "";
        public string ScaleJson { get; set; } = "[]";
        public string ContentHash { get; set; } = "";
        public long CreatedUtc { get; set; }
        public long CreatedOffset { get; set; }

        public QuestionVersion ToVersion() => new(
            Id, QuestionId, Text,
            JsonSerializer.Deserialize<string[]>(ScaleJson) ?? [],
            ContentHash,
            DbTime.From(CreatedUtc, CreatedOffset));
    }

    private sealed class QuestionRow : VersionRow
    {
        public long SortOrder { get; set; }
        public long Archived { get; set; }
    }
}
