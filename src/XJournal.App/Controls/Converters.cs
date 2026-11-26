using Avalonia.Data.Converters;
using Avalonia.Media;

namespace XJournal.App.Controls;

public static class Converters
{
    public static readonly IValueConverter BoolToSemiBold =
        new FuncValueConverter<bool, FontWeight>(value => value ? FontWeight.SemiBold : FontWeight.Normal);
}
