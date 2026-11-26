using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using XJournal.App.Controls;
using XJournal.App.ViewModels;

namespace XJournal.App.Views;

public partial class MainWindow : JournalWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => ViewModel?.SaveNow();
        Closing += (_, _) => ViewModel?.Close();
        Opened += (_, _) => FocusEditor();
        CloseSearchButton.Click += (_, _) => ClosePane();
        CloseCalendarButton.Click += (_, _) => ClosePane();
        foreach (var list in new[] { ResultsList, DayList })
        {
            list.Tapped += (_, _) => OpenSelected(list);
            list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { OpenSelected(list); e.Handled = true; }
            };
        }
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && ResultsList.ItemCount > 0)
            {
                ResultsList.SelectedIndex = 0;
                ResultsList.ContainerFromIndex(0)?.Focus();
                e.Handled = true;
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && ViewModel is { IsEditorOpen: false }) { ClosePane(); e.Handled = true; }
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is not { } vm) return;
        vm.EditorFocusRequested += FocusEditor;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(MainViewModel.ActivePane)) return;
            if (vm.IsSearchOpen) Dispatcher.UIThread.Post(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
            else if (vm.IsEditorOpen) FocusEditor();
        };
    }

    private void ClosePane()
    {
        if (ViewModel is { } vm) vm.ActivePane = MainPane.Editor;
    }

    private void OpenSelected(ListBox list)
    {
        if (list.SelectedItem is SearchResultItem item) ViewModel?.OpenResult(item);
    }

    private void FocusEditor() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel is { IsEditorOpen: false }) return;
            Editor.Focus();
            Editor.CaretIndex = Editor.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
}
