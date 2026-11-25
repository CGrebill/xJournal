namespace XJournal.Core;

/// <summary>Base type for expected, user-presentable failures.</summary>
public class JournalException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The password did not decrypt the file (or the file is not an xJournal database).</summary>
public sealed class InvalidPasswordException(Exception? inner = null)
    : JournalException("Incorrect password.", inner);

/// <summary>The file decrypted but is not an xJournal journal.</summary>
public sealed class NotAJournalException(string message = "This file is not an xJournal journal.")
    : JournalException(message);

/// <summary>The journal was written by a newer version of xJournal.</summary>
public sealed class NewerVersionException(int fileVersion, int supportedVersion)
    : JournalException($"This journal was created by a newer version of xJournal (format {fileVersion}; this version supports up to {supportedVersion}). Please update xJournal.");

public sealed class AccountException(string message) : JournalException(message);
