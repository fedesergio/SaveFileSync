using System.Windows;
using SaveFileSync.App.Views;
using SaveFileSync.Windows;

namespace SaveFileSync.App;

/// <summary>
/// Dev-only smoke test (<c>--selftest-ui</c>): XAML is parsed at runtime, so this opens and
/// closes every window once and reports failures on the console. Exit code 0 = all good.
/// </summary>
internal static class UiSelfTest
{
    public static async Task<int> RunAsync(AppHost host)
    {
        ConsoleAttach.AttachToParent();
        var failures = 0;
        await Task.Delay(TimeSpan.FromSeconds(2)); // let the service publish a first status

        var unitId = host.Service!.CurrentStatus.FirstOrDefault()?.UnitId;
        var factories = new List<(string Name, Func<Window> Create)>
        {
            ("MainWindow", () => new MainWindow(host)),
            ("SetupWindow", () => new SetupWindow(host)),
            ("AddUnitWindow", () => new AddUnitWindow(host)),
            ("SettingsWindow", () => new SettingsWindow(host)),
            ("ActivityWindow", () => new ActivityWindow(host)),
        };
        if (unitId is not null)
        {
            factories.Add(("UnitDetailsWindow", () => new UnitDetailsWindow(host, unitId)));
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

        Console.WriteLine(failures == 0 ? "UI self-test passed." : $"UI self-test: {failures} failure(s).");
        return failures == 0 ? 0 : 1;
    }
}
