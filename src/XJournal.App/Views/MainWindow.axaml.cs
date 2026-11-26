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
        CloseSearchButton.Click += (_, _) => CloseSearch();
        ResultsList.Tapped += (_, _) => OpenSelectedResult();
        ResultsList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenSelectedResult(); e.Handled = true; }
        };
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
            if (e.Key == Key.Escape && ViewModel is { IsSearchOpen: true }) { CloseSearch(); e.Handled = true; }
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
            if (args.PropertyName != nameof(MainViewModel.IsSearchOpen)) return;
            if (vm.IsSearchOpen) Dispatcher.UIThread.Post(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
            else FocusEditor();
        };
    }

    private void CloseSearch()
    {
        if (ViewModel is { } vm) vm.IsSearchOpen = false;
    }

    private void OpenSelectedResult()
    {
        if (ResultsList.SelectedItem is SearchResultItem item) ViewModel?.Search.OpenCommand.Execute(item);
    }

    private void FocusEditor() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel is { IsSearchOpen: true }) return;
            Editor.Focus();
            Editor.CaretIndex = Editor.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
}
