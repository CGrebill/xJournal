using Avalonia;
using Avalonia.Headless;
using XJournal.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace XJournal.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
