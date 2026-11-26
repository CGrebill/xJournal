using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace XJournal.App.Controls;

/// <summary>
/// Base window: draws content into the title bar and asks for Mica (Windows 11), then acrylic,
/// falling back to a solid theme background where the platform has neither.
/// </summary>
public class JournalWindow : Window
{
    public const double TitleBarHeight = 36;

    public const TitleBarDecorations CaptionButtonsOnly =
        TitleBarDecorations.MinimizeButton | TitleBarDecorations.MaximizeButton | TitleBarDecorations.CloseButton;

    private static readonly Lazy<WindowIcon> AppIcon =
        new(() => new WindowIcon(AssetLoader.Open(new Uri("avares://xJournal/Assets/xjournal.ico"))));

    /// <summary>
    /// The theme's drawn title bar, minus its title text when the title is turned off. FluentAvalonia's
    /// template ignores that flag (Avalonia's own themes honour it). App styles can't fix it: the drawn
    /// title bar sits outside the window and only takes its control theme.
    /// </summary>
    private static ControlTheme? CreateDecorationsTheme()
    {
        if (Application.Current?.TryFindResource(typeof(WindowDrawnDecorations), out var found) != true || found is not ControlTheme baseTheme)
            return null;
        var theme = new ControlTheme(typeof(WindowDrawnDecorations)) { BasedOn = baseTheme };
        theme.Children.Add(new Style(x => x.Nesting().Not(s => s.Class(":has-title")).Template().OfType<TextBlock>().Name("TitleTextBlock"))
        {
            Setters = { new Setter(IsVisibleProperty, false) },
        });
        return theme;
    }

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
        // Where Avalonia draws the title bar itself (e.g. Windows with an extended client area), it would
        // also draw the window title (on top of our own title text) and a full-screen button.
        // Keep just the standard caption buttons; the title still shows in the taskbar and Alt+Tab.
        WindowDrawnDecorations.SetTitleBarDecorations(this, CaptionButtonsOnly);
        if (CreateDecorationsTheme() is { } decorationsTheme) WindowDecorationsTheme = decorationsTheme;
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
