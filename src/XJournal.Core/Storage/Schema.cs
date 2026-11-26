using Dapper;
using Microsoft.Data.Sqlite;

namespace XJournal.Core.Storage;

/// <summary>Forward-only schema migrations, tracked with PRAGMA user_version.</summary>
internal static class Schema
{
    private static readonly string[] Migrations =
    [
        // ---- v1 ----------------------------------------------------------------------------
        """
        CREATE TABLE entries (
            id              INTEGER PRIMARY KEY,
            uid             TEXT    NOT NULL UNIQUE,
            created_utc     INTEGER NOT NULL,   -- unix milliseconds
            created_offset  INTEGER NOT NULL,   -- local UTC offset in minutes at creation
            updated_utc     INTEGER NOT NULL,
            updated_offset  INTEGER NOT NULL,
            summary         TEXT    NOT NULL DEFAULT '',
            body            TEXT    NOT NULL DEFAULT ''
        );
        CREATE INDEX ix_entries_created ON entries(created_utc);
        CREATE INDEX ix_entries_updated ON entries(updated_utc);

        CREATE VIRTUAL TABLE entries_fts USING fts5(
            summary, body,
            content = 'entries', content_rowid = 'id',
            tokenize = 'unicode61 remove_diacritics 2'
        );
        CREATE TRIGGER entries_ai AFTER INSERT ON entries BEGIN
            INSERT INTO entries_fts(rowid, summary, body) VALUES (new.id, new.summary, new.body);
        END;
        CREATE TRIGGER entries_ad AFTER DELETE ON entries BEGIN
            INSERT INTO entries_fts(entries_fts, rowid, summary, body) VALUES ('delete', old.id, old.summary, old.body);
        END;
        CREATE TRIGGER entries_au AFTER UPDATE OF summary, body ON entries BEGIN
            INSERT INTO entries_fts(entries_fts, rowid, summary, body) VALUES ('delete', old.id, old.summary, old.body);
            INSERT INTO entries_fts(rowid, summary, body) VALUES (new.id, new.summary, new.body);
        END;

        -- Logical questions, keyed by the stable id from the import file. Never deleted, only archived.
        CREATE TABLE questions (
            id                  TEXT    PRIMARY KEY,
            sort_order          INTEGER NOT NULL,
            archived            INTEGER NOT NULL DEFAULT 0,
            current_version_id  INTEGER REFERENCES question_versions(id)
        );
        CREATE TRIGGER questions_no_delete BEFORE DELETE ON questions BEGIN
            SELECT RAISE(ABORT, 'Questions cannot be deleted; archive them instead.');
        END;

        -- Exact wording + scale ever shown for a question. Immutable: answers point here.
        CREATE TABLE question_versions (
            id              INTEGER PRIMARY KEY,
            question_id     TEXT    NOT NULL REFERENCES questions(id),
            text            TEXT    NOT NULL,
            scale_json      TEXT    NOT NULL,
            content_hash    TEXT    NOT NULL,
            created_utc     INTEGER NOT NULL,
            created_offset  INTEGER NOT NULL,
            UNIQUE (question_id, content_hash)
        );
        CREATE TRIGGER question_versions_immutable BEFORE UPDATE ON question_versions BEGIN
            SELECT RAISE(ABORT, 'Question versions are immutable.');
        END;
        CREATE TRIGGER question_versions_no_delete BEFORE DELETE ON question_versions BEGIN
            SELECT RAISE(ABORT, 'Question versions cannot be deleted.');
        END;

        -- One answer set per local calendar day, linked to the entry that was open.
        CREATE TABLE answer_sets (
            id              INTEGER PRIMARY KEY,
            day             TEXT    NOT NULL UNIQUE,     -- yyyy-MM-dd (local)
            entry_id        INTEGER REFERENCES entries(id) ON DELETE SET NULL,
            answered_utc    INTEGER NOT NULL,
            answered_offset INTEGER NOT NULL
        );

        CREATE TABLE answers (
            answer_set_id       INTEGER NOT NULL REFERENCES answer_sets(id) ON DELETE CASCADE,
            question_version_id INTEGER NOT NULL REFERENCES question_versions(id),
            value               INTEGER NOT NULL CHECK (value BETWEEN 1 AND 5),
            PRIMARY KEY (answer_set_id, question_version_id)
        );
        CREATE INDEX ix_answers_version ON answers(question_version_id);

        CREATE TABLE settings (
            key     TEXT PRIMARY KEY,
            value   TEXT NOT NULL
        );
        """,

        // ---- v2: previous versions of entries (kept when a merge or restore replaces text) ----
        """
        CREATE TABLE entry_revisions (
            id              INTEGER PRIMARY KEY,
            entry_id        INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
            summary         TEXT    NOT NULL,
            body            TEXT    NOT NULL,
            updated_utc     INTEGER NOT NULL,   -- when this text was last edited
            updated_offset  INTEGER NOT NULL,
            saved_utc       INTEGER NOT NULL,   -- when it became a previous version
            saved_offset    INTEGER NOT NULL,
            reason          TEXT    NOT NULL    -- 'merge' or 'restore'
        );
        CREATE INDEX ix_revisions_entry ON entry_revisions(entry_id);
        """,

        // ---- v3: question types (rating, scale, yes/no, choice, number, text) ---------------
        // Existing questions were all labeled 1–5 ratings: their labels move into config_json.
        // scale_json is kept (rating labels, '[]' for other types) so older code paths stay simple.
        """
        ALTER TABLE question_versions ADD COLUMN type TEXT NOT NULL DEFAULT 'rating';
        ALTER TABLE question_versions ADD COLUMN config_json TEXT NOT NULL DEFAULT '{}';
        DROP TRIGGER question_versions_immutable;
        UPDATE question_versions SET config_json = '{"labels":' || scale_json || '}';
        CREATE TRIGGER question_versions_immutable BEFORE UPDATE ON question_versions BEGIN
            SELECT RAISE(ABORT, 'Question versions are immutable.');
        END;

        -- Answers hold a number (rating, scale, number; yes/no as 1/0) or text (text; choice as a JSON array).
        CREATE TABLE answers_v3 (
            answer_set_id       INTEGER NOT NULL REFERENCES answer_sets(id) ON DELETE CASCADE,
            question_version_id INTEGER NOT NULL REFERENCES question_versions(id),
            value_num           REAL,
            value_text          TEXT,
            CHECK ((value_num IS NULL) <> (value_text IS NULL)),
            PRIMARY KEY (answer_set_id, question_version_id)
        );
        INSERT INTO answers_v3 (answer_set_id, question_version_id, value_num)
            SELECT answer_set_id, question_version_id, value FROM answers;
        DROP TABLE answers;
        ALTER TABLE answers_v3 RENAME TO answers;
        CREATE INDEX ix_answers_version ON answers(question_version_id);
        """,
    ];

    public static int CurrentVersion => Migrations.Length;

    public static void Migrate(SqliteConnection connection) => Migrate(connection, CurrentVersion);

    /// <summary>Migrates up to <paramref name="target"/> (tests use this to build older databases).</summary>
    internal static void Migrate(SqliteConnection connection, int target)
    {
        var version = connection.ExecuteScalar<int>("PRAGMA user_version");
        if (version > CurrentVersion) throw new NewerVersionException(version, CurrentVersion);

        for (var next = version; next < target; next++)
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(Migrations[next], transaction: transaction);
            connection.Execute($"PRAGMA user_version = {next + 1}", transaction: transaction);
            if (next == 0)
                connection.Execute($"PRAGMA application_id = {JournalDatabase.ApplicationId}", transaction: transaction);
            transaction.Commit();
        }
    }
}
