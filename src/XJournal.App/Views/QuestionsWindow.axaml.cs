using Avalonia.Controls;
using XJournal.App.Controls;
using XJournal.App.ViewModels;

namespace XJournal.App.Views;

public partial class QuestionsWindow : JournalWindow
{
    public QuestionsWindow() => InitializeComponent();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Typed answers save after a short pause; don't lose one typed just before closing.
        (DataContext as QuestionsViewModel)?.SaveNow();
        base.OnClosing(e);
    }
}
