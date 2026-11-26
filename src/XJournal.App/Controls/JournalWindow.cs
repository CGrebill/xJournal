using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace XJournal.App.Controls;

/// <summary>
/// Base window: draws content into the title bar and asks for Mica (Windows 11), then acrylic,
/// falling back to a solid theme background where the platform has neither.
/// </summary>
public class JournalWindow : Window
{
    public const double TitleBarHeight = 36;

    private static readonly Lazy<WindowIcon> AppIcon =
        new(() => new WindowIcon(AssetLoader.Open(new Uri("avares://xJournal/Assets/xjournal.ico"))));

    protected override Type StyleKeyOverride => typeof(Window);

    /// <summary>Raised on any keyboard or pointer input in any xJournal window (used for auto-lock).</summary>
    public static event Action? Activity;

    public JournalWindow()
    {
        AddHandler(KeyDownEvent, (_, _) => Activity?.Invoke(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => Activity?.Invoke(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, (_, _) => Activity?.Invoke(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, (_, _) => Activity?.Invoke(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None];
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        Classes.Add("journal");
        if (OperatingSystem.IsMacOS()) Classes.Add("macos");
        Icon = AppIcon.Value;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActualTransparencyLevelProperty || change.Property == ActualThemeVariantProperty)
            UpdateBackdrop();
    }

    /// <summary>With no system backdrop, paint the theme's base background so content stays readable.</summary>
    private void UpdateBackdrop()
    {
        var hasBackdrop = ActualTransparencyLevel != WindowTransparencyLevel.None;
        Classes.Set("backdrop", hasBackdrop);
        if (!this.TryFindResource("SolidBackgroundFillColorBaseBrush", ActualThemeVariant, out var resource) || resource is not IBrush solid)
            return;
        TransparencyBackgroundFallback = solid;
        Background = hasBackdrop ? Brushes.Transparent : solid;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateBackdrop();
    }
}
