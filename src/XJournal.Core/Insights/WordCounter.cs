namespace XJournal.Core.Insights;

public static class WordCounter
{
    /// <summary>Counts runs of non-whitespace characters.</summary>
    public static int Count(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var words = 0;
        var inWord = false;
        foreach (var c in text)
        {
            var isSpace = char.IsWhiteSpace(c);
            if (!isSpace && !inWord) words++;
            inWord = !isSpace;
        }
        return words;
    }
}
