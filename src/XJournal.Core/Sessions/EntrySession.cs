using XJournal.Core.Models;

namespace XJournal.Core.Sessions;

/// <summary>
/// Tracks the entry being written and handles everything automatic about it:
/// resuming a recent entry on sign-in, creating new entries, saving, and discarding empty ones.
/// The caller decides <em>when</em> to save (e.g. after a typing pause); this class decides <em>how</em>.
/// </summary>
public sealed class EntrySession
{
    private readonly Journal _journal;

    public EntrySession(Journal journal)
    {
        _journal = journal;
        Current = NewDraft();
    }

    /// <summary>The entry shown in the editor. Not persisted until it has some text.</summary>
    public JournalEntry Current { get; private set; }

    /// <summary>True when the editor has changes that haven't been saved.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Raised when <see cref="Current"/> is replaced by another entry.</summary>
    public event EventHandler? CurrentChanged;

    /// <summary>Raised after the current entry is written to the database.</summary>
    public event EventHandler? Saved;

    /// <summary>
    /// Called on sign-in: reopens the most recently edited entry if it was edited within the resume
    /// window, otherwise starts a fresh entry.
    /// </summary>
    public void Begin()
    {
        var latest = _journal.Entries.GetLatest();
        Current = latest is not null && ShouldResume(latest) ? latest : NewDraft();
        IsDirty = false;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    internal bool ShouldResume(JournalEntry latest)
    {
        var age = _journal.Clock.Now - latest.UpdatedAt;
        return age >= TimeSpan.Zero && age < _journal.Settings.ResumeWindow;
    }

    /// <summary>Updates the in-memory text. An unsaved entry's timestamp is the moment writing starts.</summary>
    public void SetText(string summary, string body)
    {
        summary ??= "";
        body ??= "";
        if (summary == Current.Summary && body == Current.Body) return;

        var wasEmpty = Current.IsEmpty;
        Current.Summary = summary;
        Current.Body = body;
        IsDirty = true;
        if (!Current.IsPersisted && wasEmpty && !Current.IsEmpty)
            Current.CreatedAt = _journal.Clock.LocalNow();
    }

    /// <summary>Writes pending changes. An empty, never-saved entry is not written.</summary>
    /// <returns>True if anything was written.</returns>
    public bool Save()
    {
        if (!IsDirty) return false;
        var now = _journal.Clock.LocalNow();
        if (!Current.IsPersisted)
        {
            if (Current.IsEmpty)
            {
                IsDirty = false;
                return false;
            }
            Current.UpdatedAt = now;
            _journal.Entries.Insert(Current);
            // If today's questions were answered before this entry existed, link them to it.
            _journal.Answers.AttachEntryIfMissing(_journal.Clock.Today(), Current.Id);
        }
        else
        {
            Current.UpdatedAt = now;
            _journal.Entries.Update(Current);
        }

        IsDirty = false;
        Saved?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Saves the current entry and switches to a fresh one.</summary>
    public void StartNew()
    {
        Leave();
        Current = NewDraft();
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves the current entry and opens another one.</summary>
    public void Open(long entryId)
    {
        if (Current.Id == entryId) return;
        var entry = _journal.Entries.Get(entryId) ?? throw new JournalException("That entry no longer exists.");
        Leave();
        Current = entry;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Permanently deletes the current entry and starts a fresh one.</summary>
    public void DeleteCurrent()
    {
        if (Current.IsPersisted) _journal.Entries.Delete(Current.Id);
        IsDirty = false;
        Current = NewDraft();
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Saves and closes the current entry (on sign-out, New, or opening another entry).
    /// A saved entry whose text was completely cleared is removed.
    /// </summary>
    public void Leave()
    {
        Save();
        if (Current.IsPersisted && Current.IsEmpty)
        {
            _journal.Entries.Delete(Current.Id);
            Current.Id = 0;
        }
    }

    private JournalEntry NewDraft()
    {
        var now = _journal.Clock.LocalNow();
        return new JournalEntry { CreatedAt = now, UpdatedAt = now };
    }
}
