using System.Diagnostics;
using System.Windows;
using SaveFileSync.App.Views;
using SaveFileSync.Windows;

namespace SaveFileSync.App;

/// <summary>Collects WPF data-binding errors, which are otherwise only written to a trace log.</summary>
internal sealed class BindingErrorListener : TraceListener
{
    public List<string> Errors { get; } = [];

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Errors.Add(message.Trim());
        }
    }
}

/// <summary>
/// Dev-only smoke test (<c>--selftest-ui</c>): XAML is parsed at runtime, so this opens and
/// closes every window once, exercises the add-game flow and reports failures on the console.
/// Exit code 0 = all good.
/// </summary>
internal static class UiSelfTest
{
    public static async Task<int> RunAsync(AppHost host)
    {
        ConsoleAttach.AttachToParent();
        var failures = 0;
        var bindings = new BindingErrorListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        await Task.Delay(TimeSpan.FromSeconds(2)); // let the service publish a first status

        var unitId = host.Service!.CurrentStatus.FirstOrDefault()?.UnitId;
        var factories = new List<(string Name, Func<Window> Create)>
        {
            ("MainWindow", () => new MainWindow(host)),
            ("SetupWindow", () => new SetupWindow(host)),
            ("AddUnitWindow", () => new AddUnitWindow(host)),
            ("SettingsWindow", () => new SettingsWindow(host)),
            ("ActivityWindow", () => new ActivityWindow(host)),
            ("DevicesWindow", () => new DevicesWindow(host)),
        };
        if (unitId is not null)
        {
            factories.Add(("UnitDetailsWindow", () => new UnitDetailsWindow(host, unitId)));
            factories.Add(("HistoryWindow", () => new HistoryWindow(host, unitId)));
            factories.Add(("ConflictWindow", () => new ConflictWindow(host, unitId)));
        }

        foreach (var (name, create) in factories)
        {
            try
            {
                var window = create();
                window.ShowActivated = false;
                window.Show();
                await Task.Delay(500);

                IReadOnlyList<string> problems = window switch
                {
                    AddUnitWindow add => await add.SelfTestAsync(),
                    HistoryWindow history => await history.SelfTestAsync(),
                    _ => [],
                };
                foreach (var problem in problems)
                {
                    failures++;
                    Console.WriteLine($"FAIL  {name}: {problem}");
                }

                window.Close();
                Console.WriteLine($"OK    {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
                Serilog.Log.Error(ex, "UI self-test failed for {Window}", name);
            }
        }

        foreach (var error in bindings.Errors.Distinct())
        {
            failures++;
            Console.WriteLine($"FAIL  binding: {error}");
        }

        Console.WriteLine(failures == 0 ? "UI self-test passed." : $"UI self-test: {failures} failure(s).");
        return failures == 0 ? 0 : 1;
    }
}
