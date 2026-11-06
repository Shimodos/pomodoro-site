using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PomodoroTimer.Models;
using PomodoroTimer.Services;
using PomodoroTimer.ViewModels;
using PomodoroTimer.Views;

namespace Snapshot;

/// <summary>
/// Loads the Pomodoro app in-process, puts its windows into a few typical states and renders
/// them with RenderTargetBitmap at a given scale. The output is pixel-crisp at any DPI, which
/// a PrintWindow screenshot of a 100% display can't give.
/// </summary>
internal static class Program
{
    private static double _scale = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "shots";
        if (args.Length > 1) _scale = double.Parse(args[1], CultureInfo.InvariantCulture);
        Directory.CreateDirectory(outDir);

        // Keep the real user settings untouched: point the app at a scratch folder.
        string data = Path.Combine(Path.GetTempPath(), "pomodoro-snapshot");
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("POMODORO_DATA_DIR", data);

        // Relative pack URIs ("Localization/Strings.en.xaml") resolve against the entry assembly,
        // which is this tool; point WPF at the app assembly instead.
        // The public setter refuses once the entry assembly has been read, so set the backing field.
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(PomodoroTimer.App).Assembly);

        var app = new PomodoroTimer.App();
        app.InitializeComponent(); // merges Strings / theme / styles dictionaries
        SettingsService.Load();

        var s = SettingsService.Current;
        s.Language = "en";
        s.Theme = "Light";
        s.Accent = "#FF6B6B";
        s.BreakAccent = "auto";
        s.LimitSessions = true;
        s.TotalSessions = 4;
        s.CompactMode = false;
        s.WindowLeft = null;
        s.WindowTop = null;
        Localizer.Apply(s.Language);
        ThemeManager.Apply(s);

        var vm = new MainViewModel();
        var main = new MainWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
        };
        main.Show();
        Pump(400);

        var engine = GetEngine(vm);

        // ---- focus running, session 2 of 4, 17:24 left (hero)
        vm.ToggleCommand.Execute(null);
        SetState(engine, completed: 1, remaining: TimeSpan.FromSeconds(17 * 60 + 24));
        Pump(300);
        Save(main, Path.Combine(outDir, "focus-light.png"));

        SetTheme(s, vm, "Dark");
        Save(main, Path.Combine(outDir, "focus-dark.png"));

        // ---- looking at the break tab while focus keeps running
        vm.ViewedPhase = TimerPhase.ShortBreak;
        Pump(300);
        Save(main, Path.Combine(outDir, "break-view-dark.png"));
        SetTheme(s, vm, "Light");
        Save(main, Path.Combine(outDir, "break-view-light.png"));
        vm.ViewedPhase = TimerPhase.Work;

        // ---- short break running, 2 sessions done, 03:41 left
        vm.SkipCommand.Execute(null); // work -> short break (a manual skip doesn't auto-start)
        if (!engine.IsRunning) vm.ToggleCommand.Execute(null);
        SetState(engine, completed: 2, remaining: TimeSpan.FromSeconds(3 * 60 + 41));
        Pump(300);
        Save(main, Path.Combine(outDir, "break-light.png"));
        SetTheme(s, vm, "Dark");
        Save(main, Path.Combine(outDir, "break-dark.png"));

        // ---- compact mode (break running, dark)
        vm.IsCompact = true;
        Pump(400);
        Save(main, Path.Combine(outDir, "compact-dark.png"));
        SetTheme(s, vm, "Light");
        Save(main, Path.Combine(outDir, "compact-light.png"));
        vm.IsCompact = false;
        Pump(400);

        // ---- editing the focus length in place (timer stopped)
        vm.ResetCommand.Execute(null);
        vm.ViewedPhase = TimerPhase.Work;
        Pump(200);
        vm.BeginEditCommand.Execute(null);
        Pump(300);
        Save(main, Path.Combine(outDir, "edit-light.png"));
        SetTheme(s, vm, "Dark");
        Save(main, Path.Combine(outDir, "edit-dark.png"));
        vm.CancelEditCommand.Execute(null);
        SetTheme(s, vm, "Light");

        // ---- settings pages
        var settings = new SettingsWindow(vm)
        {
            Owner = main,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
        };
        settings.Show();
        Pump(500);
        foreach (var (nav, name) in new[] { ("NavTimer", "timer"), ("NavSound", "sound"), ("NavLook", "look"), ("NavSystem", "system") })
        {
            if (settings.FindName(nav) is RadioButton rb) rb.IsChecked = true;
            Pump(300);
            Save(settings, Path.Combine(outDir, $"settings-{name}-light.png"));
        }
        if (settings.FindName("NavLook") is RadioButton look) look.IsChecked = true;
        if (settings.DataContext is SettingsViewModel draft) draft.Theme = "Dark"; // live preview, like the real dialog
        Pump(400);
        Save(settings, Path.Combine(outDir, "settings-look-dark.png"));
        // the other pages in the dark theme, for the site's dark mode
        foreach (var (nav, name) in new[] { ("NavTimer", "timer"), ("NavSound", "sound"), ("NavSystem", "system") })
        {
            if (settings.FindName(nav) is RadioButton rb) rb.IsChecked = true;
            Pump(300);
            Save(settings, Path.Combine(outDir, $"settings-{name}-dark.png"));
        }
        settings.Close();

        vm.Dispose();

        SaveOg(Path.Combine(outDir, "focus-light.png"), Path.Combine(outDir, "og.png"));
        Console.WriteLine($"done -> {Path.GetFullPath(outDir)}");
        return 0;
    }

    /// <summary>1200x630 preview card for X / Telegram / Slack: title on the left, the app on the right.</summary>
    private static void SaveOg(string shotPath, string outPath)
    {
        const int W = 1200, H = 630;
        var shot = new BitmapImage(new Uri(Path.GetFullPath(shotPath)));
        var ink = (Brush)new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1d));
        var muted = (Brush)new SolidColorBrush(Color.FromRgb(0x5c, 0x63, 0x70));
        var coral = (Brush)new SolidColorBrush(Color.FromRgb(0xff, 0x6b, 0x6b));
        var font = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var fontRegular = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xf7, 0xf7, 0xf9)), null, new Rect(0, 0, W, H));

            // accent dot + name
            dc.DrawEllipse(coral, null, new Point(96, 112), 14, 14);
            dc.DrawText(Text("Pomodoro", font, 34, ink), new Point(124, 90));

            dc.DrawText(Text("Focus. Break.\nRepeat.", font, 84, ink, 640), new Point(92, 170));
            dc.DrawText(Text("A native Pomodoro timer for Windows.", fontRegular, 32, muted, 640), new Point(96, 392));
            dc.DrawText(Text("Free  ·  Open source  ·  Windows 10 / 11  ·  No install", fontRegular, 24, muted, 640), new Point(96, 470));

            // the window, 2x shot drawn at ~0.5 so it stays crisp, with a soft shadow and rounded corners
            double scale = 0.56;
            double w = shot.PixelWidth * scale / 2, h = shot.PixelHeight * scale / 2;
            var rect = new Rect(W - w - 88, 64, w, h);
            // a soft shadow: many faint, slightly larger copies of the frame
            for (int i = 24; i >= 1; i--)
            {
                var shadow = new SolidColorBrush(Color.FromArgb(3, 0, 0, 0));
                dc.DrawRoundedRectangle(shadow, null, new Rect(rect.X - i, rect.Y - i + i * 0.6, rect.Width + i * 2, rect.Height + i * 2), 14 + i, 14 + i);
            }
            dc.PushClip(new RectangleGeometry(rect, 14, 14));
            dc.DrawImage(shot, rect);
            dc.Pop();
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)), 1), rect, 14, 14);
        }

        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(outPath);
        encoder.Save(stream);
        Console.WriteLine($"{Path.GetFileName(outPath)} {W}x{H}");
    }

    private static FormattedText Text(string text, Typeface face, double size, Brush brush, double maxWidth = 1000)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, 1.0)
        {
            MaxTextWidth = maxWidth,
        };
        return ft;
    }

    private static void SetTheme(AppSettings s, MainViewModel vm, string theme)
    {
        s.Theme = theme;
        ThemeManager.Apply(s);
        vm.RefreshView();
        Pump(300);
    }

    private static TimerEngine GetEngine(MainViewModel vm)
    {
        var field = typeof(MainViewModel).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainViewModel._engine not found");
        return (TimerEngine)field.GetValue(vm)!;
    }

    /// <summary>Puts the engine mid-session: N work sessions done, a given time left on the running phase.</summary>
    private static void SetState(TimerEngine engine, int completed, TimeSpan remaining)
    {
        const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        typeof(TimerEngine).GetProperty("CompletedWork", f)!.SetValue(engine, completed);
        typeof(TimerEngine).GetField("_endUtc", f)!.SetValue(engine, DateTime.UtcNow + remaining);
        typeof(TimerEngine).GetField("_remaining", f)!.SetValue(engine, remaining);
    }

    private static void Save(Window window, string path)
    {
        // A window that just changed size (compact mode) still carries the old layout in its
        // visual tree; without this the VisualBrush squashes the old content into the new box.
        window.UpdateLayout();
        Pump(100);
        int w = (int)Math.Round(window.ActualWidth * _scale);
        int h = (int)Math.Round(window.ActualHeight * _scale);
        var rtb = new RenderTargetBitmap(w, h, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32);

        // Paint the window background first: the Window visual alone may render transparent.
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var rect = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
            dc.DrawRectangle(window.Background, null, rect);
            // No stretching: paint the visual 1:1 from the top-left corner, whatever its bounds are.
            var brush = new VisualBrush(window) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            dc.DrawRectangle(brush, null, rect);
        }
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"{Path.GetFileName(path)} {w}x{h}");
    }

    /// <summary>Lets bindings, layout and the 200 ms engine tick settle.</summary>
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
