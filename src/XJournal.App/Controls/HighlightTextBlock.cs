using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using XJournal.Core.Models;

namespace XJournal.App.Controls;

/// <summary>Shows a search snippet, emphasising the matched words.</summary>
public class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> MarkedTextProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(MarkedText));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>Text with matches wrapped in <see cref="EntrySearchResult.HighlightStart"/>/<see cref="EntrySearchResult.HighlightEnd"/>.</summary>
    public string? MarkedText
    {
        get => GetValue(MarkedTextProperty);
        set => SetValue(MarkedTextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkedTextProperty) Rebuild();
    }

    private void Rebuild()
    {
        var inlines = new InlineCollection();
        var text = MarkedText ?? "";
        var highlighted = false;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var atEnd = i == text.Length;
            if (!atEnd && text[i] != EntrySearchResult.HighlightStart && text[i] != EntrySearchResult.HighlightEnd) continue;
            if (i > start)
            {
                var run = new Run(text[start..i].Replace('\n', ' '));
                if (highlighted)
                {
                    run.FontWeight = FontWeight.SemiBold;
                    run[!Run.ForegroundProperty] = this.GetResourceObservable("AccentTextFillColorPrimaryBrush").ToBinding();
                }
                inlines.Add(run);
            }
            if (!atEnd) highlighted = text[i] == EntrySearchResult.HighlightStart;
            start = i + 1;
        }
        Inlines = inlines;
    }
}
