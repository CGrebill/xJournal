using XJournal.App.Controls;
using XJournal.App.ViewModels;

namespace XJournal.App.Views;

public partial class TrendsWindow : JournalWindow
{
    public TrendsWindow()
    {
        InitializeComponent();
        Chart.PointActivated += point => (DataContext as TrendsViewModel)?.OpenEntry(point);
    }
}
