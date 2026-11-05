using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PomodoroTimer.Services;
using PomodoroTimer.ViewModels;
using PomodoroTimer.Views;
using Path = System.IO.Path;

namespace Promo;

/// <summary>A window captured at 2x, plus its logical size.</summary>
internal sealed record Shot(BitmapSource Image, double W, double H);

/// <summary>Everything one frame of a clip shows: a window (optionally cross-fading into a second one) and a cursor.</summary>
internal sealed class Frame
{
    public Shot A = null!;
    public Rect RectA;
    public Shot? B;
    public Rect RectB;
    public double Morph;      // 0 = A only, 1 = B only
    public double Pop = 1;    // extra scale of the window (the "pop" when a phase ends)
    public Point? Cursor;     // frame pixels
    public bool Pressed;
}

internal sealed record Clip(string Name, double Duration, double Zoom, Func<double, Frame> At);

/// <summary>
/// Renders the promo clips. The app has no continuous animations of its own (the clock steps
/// once a second, the ring once per tick), so the windows are captured in a handful of states
/// and every frame is composed offline: gradient backdrop, shadow, a slow camera push-in, a
/// drawn cursor. ffmpeg encodes the frames streamed through stdin.
/// </summary>
internal static class Program
{
    // Output frame: 4:5 portrait like the reference clip, H.264-friendly even size.
    private const int W = 1080, H = 1350, Fps = 30;

    // Windows are captured at 2x; the main window is placed 1:1 at camera scale 1, so it stays crisp.
    private const double ShotScale = 2;
    private const double ClickHold = 0.15;

    [STAThread]
    private static int Main(string[] args)
    {
        string outFile = "promo.mp4";
        string clipName = "main";
        string ffmpeg = "ffmpeg";
        string? framesDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-clip": clipName = args[++i]; break;
                case "-ffmpeg": ffmpeg = args[++i]; break;
                case "-frames": framesDir = args[++i]; break;
                default: outFile = args[i]; break;
            }
        }

        var (vm, main) = Boot();
        var clip = clipName switch
        {
            "main" => MainClip(vm, main),
            "settings" => SettingsClip(vm, main),
            _ => throw new ArgumentException($"unknown clip '{clipName}' (main | settings)"),
        };
        vm.Dispose();

        Render(clip, outFile, ffmpeg, framesDir);
        Console.WriteLine($"done -> {Path.GetFullPath(outFile)}");
        return 0;
    }

    // ------------------------------------------------------------------ the app

    private static (MainViewModel vm, MainWindow main) Boot()
    {
        // Keep the real user settings untouched: point the app at a scratch folder.
        string data = Path.Combine(Path.GetTempPath(), "pomodoro-promo");
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("POMODORO_DATA_DIR", data);

        // Relative pack URIs resolve against the entry assembly, which is this tool; point WPF
        // at the app assembly instead (the public setter refuses once it has been read).
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(PomodoroTimer.App).Assembly);

        var app = new PomodoroTimer.App();
        app.InitializeComponent();
        SettingsService.Load();

        var s = SettingsService.Current;
        s.Language = "en";
        s.Theme = "Light";
        s.Accent = "#FF6B6B";
        s.BreakAccent = "auto";
        s.WorkMinutes = 25;
        s.ShortBreakMinutes = 5;
        s.LongBreakMinutes = 15;
        s.CyclesBeforeLongBreak = 4;
        s.LimitSessions = true;
        s.TotalSessions = 4;
        s.AutoStartBreaks = true;
        s.AutoStartWork = false;
        s.CompactMode = false;
        s.AlwaysOnTop = false;
        s.StartWithWindows = false;
        s.WindowLeft = null;
        s.WindowTop = null;
        // The phase-end side effects are not wanted while rendering: no sound, no toast,
        // and no window activation (the window lives off-screen).
        s.SoundEnabled = false;
        s.NotificationsEnabled = false;
        s.ShowOnPhaseEnd = false;
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
        return (vm, main);
    }

    // ------------------------------------------------------------------ clip 1: the timer

    /// <summary>
    /// Focus at 00:04 … 00:01, the countdown completes by itself and the break auto-starts in its
    /// own color, then the cursor switches the window to compact mode.
    /// </summary>
    private static Clip MainClip(MainViewModel vm, MainWindow main)
    {
        var engine = GetEngine(vm);
        var shots = new Dictionary<string, Shot>();

        vm.ToggleCommand.Execute(null); // start focus, session 2 of 4
        for (int sec = 4; sec >= 1; sec--)
        {
            SetState(engine, completed: 1, remaining: TimeSpan.FromSeconds(sec - 0.1));
            Pump(250);
            shots[$"w{sec}"] = Capture(main);
        }
        var compactButton = CenterOf(main, Descendants<Button>(main).First(b => b.Style == main.Resources["CompactButton"]));

        // Let the engine finish the focus on its own tick: Advance -> short break -> auto-start.
        SetState(engine, completed: 1, remaining: TimeSpan.FromSeconds(-0.5));
        Pump(500);
        if (engine.Phase != TimerPhase.ShortBreak || !engine.IsRunning)
            throw new InvalidOperationException($"expected a running short break, got {engine.Phase} running={engine.IsRunning}");

        for (int sec = 300; sec >= 298; sec--)
        {
            SetState(engine, completed: 2, remaining: TimeSpan.FromSeconds(sec - 0.1));
            Pump(250);
            shots[$"b{sec}"] = Capture(main);
        }

        vm.IsCompact = true;
        Pump(400);
        for (int sec = 298; sec >= 295; sec--)
        {
            SetState(engine, completed: 2, remaining: TimeSpan.FromSeconds(sec - 0.1));
            Pump(250);
            shots[$"c{sec}"] = Capture(main);
        }

        // Timeline (seconds). The clock changes at x.9 so no change falls inside the morph.
        const double breakAt = 3.9, pressAt = 5.85, morphStart = 6.0, morphEnd = 6.5;
        var big = Centered(340, 540, ShotScale);
        var compact = Centered(250, 300, ShotScale);
        var target = ToFrame(big, ShotScale, compactButton);
        var cursor = new List<(double t, Point p)>
        {
            (4.6, new Point(W + 80, H + 80)),
            (5.7, target),
            (6.8, target),
            (7.6, new Point(W + 120, H * 0.78)),
        };

        return new Clip("main", 9.4, 1.07, t =>
        {
            string bigKey = t < 0.9 ? "w4" : t < 1.9 ? "w3" : t < 2.9 ? "w2" : t < 3.9 ? "w1" : t < 4.9 ? "b300" : t < 5.9 ? "b299" : "b298";
            string compactKey = t < 6.9 ? "c298" : t < 7.9 ? "c297" : t < 8.9 ? "c296" : "c295";
            return new Frame
            {
                A = shots[bigKey], RectA = big,
                B = shots[compactKey], RectB = compact,
                Morph = EaseInOut(Clamp01((t - morphStart) / (morphEnd - morphStart))),
                Pop = 1 + 0.035 * Math.Sin(Math.PI * Clamp01((t - breakAt) / 0.45)),
                Cursor = CursorAt(cursor, t),
                Pressed = t >= pressAt && t < pressAt + ClickHold,
            };
        });
    }

    // ------------------------------------------------------------------ clip 2: the settings

    /// <summary>
    /// A walk through the settings dialog: the focus slider is dragged, the Sound page is shown,
    /// the dark theme and a new accent are picked (live preview), "always on top" is switched on,
    /// Save closes the dialog and the main window appears in its new look.
    /// </summary>
    private static Clip SettingsClip(MainViewModel vm, MainWindow main)
    {
        var engine = GetEngine(vm);
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
        var draft = (SettingsViewModel)settings.DataContext;
        // Boot switched these off to keep the render quiet; the dialog should show the real defaults.
        draft.SoundEnabled = true;
        draft.NotificationsEnabled = true;
        draft.ShowOnPhaseEnd = true;

        RadioButton Nav(string name) => (RadioButton)settings.FindName(name);
        var pNavSound = CenterOf(settings, Nav("NavSound"));
        var pNavLook = CenterOf(settings, Nav("NavLook"));
        var pNavSystem = CenterOf(settings, Nav("NavSystem"));
        var pSave = CenterOf(settings, (Button)settings.FindName("SaveButton"));

        // ---- Timer page: drag the focus slider 25 -> 30
        var slider = Descendants<Slider>(settings).First(x => x.IsVisible);
        var thumb = Descendants<Thumb>(slider).First();
        var timer = new List<(Shot shot, Point thumb)>();
        for (int m = 25; m <= 30; m++)
        {
            draft.WorkMinutes = m;
            Pump(150);
            settings.UpdateLayout();
            timer.Add((Capture(settings), CenterOf(settings, thumb)));
        }

        // ---- Sound page
        Nav("NavSound").IsChecked = true;
        Pump(300);
        var sound = Capture(settings);

        // ---- Appearance: dark theme, then a violet accent (both preview live)
        Nav("NavLook").IsChecked = true;
        Pump(300);
        var lookLight = Capture(settings);
        var darkTab = Descendants<RadioButton>(settings).Where(r => r.GroupName == "Theme").ElementAt(1);
        var pDark = CenterOf(settings, darkTab);
        var accentList = Descendants<ListBox>(settings).First(l => l.IsVisible);
        const string accent = "#B07CFF";
        int accentIndex = Array.IndexOf(ThemeManager.Palette, accent);
        var swatch = (ListBoxItem)accentList.ItemContainerGenerator.ContainerFromIndex(accentIndex);
        var pSwatch = CenterOf(settings, swatch);
        draft.Theme = "Dark";
        Pump(400);
        var lookDark = Capture(settings);
        draft.Accent = accent;
        Pump(400);
        var lookAccent = Capture(settings);

        // ---- System: switch "always on top" on
        Nav("NavSystem").IsChecked = true;
        Pump(300);
        var system = Capture(settings);
        var onTop = Descendants<CheckBox>(settings).Where(c => c.IsVisible).ElementAt(2);
        // aim at the switch itself, which sits at the right edge of the row
        var pOnTop = onTop.TransformToAncestor(settings).Transform(new Point(onTop.ActualWidth - 22, onTop.ActualHeight / 2));
        draft.AlwaysOnTop = true;
        Pump(300);
        var systemOn = Capture(settings);

        // ---- Save: what Save_Click does, minus the dialog plumbing; then the main window in its new look
        SettingsService.Replace(draft.Draft);
        ThemeManager.Apply(draft.Draft);
        settings.Close();
        vm.RefreshView();
        vm.ToggleCommand.Execute(null);
        SetState(engine, completed: 0, remaining: TimeSpan.FromSeconds(29 * 60 + 12));
        Pump(400);
        var mainDark = Capture(main);

        // Timeline (seconds)
        const double drawScale = 1.5;
        const double dragStart = 1.2, dragEnd = 2.0;
        const double clickSound = 3.4, clickLook = 5.0, clickDark = 5.8, clickSwatch = 7.0, clickSystem = 8.4, clickOnTop = 9.3, clickSave = 10.5;
        const double morphStart = 10.6, morphEnd = 11.1;
        var dialog = Centered(600, 680, drawScale);
        var window = Centered(340, 540, ShotScale);
        Point F(Point p) => ToFrame(dialog, drawScale, p);
        var cursor = new List<(double t, Point p)>
        {
            (0.3, new Point(W + 80, H + 80)),
            (1.1, F(timer[0].thumb)),
            (dragStart, F(timer[0].thumb)),
            (dragEnd, F(timer[^1].thumb)),
            (2.8, F(timer[^1].thumb)),
            (clickSound, F(pNavSound)),
            (4.4, F(pNavSound)),
            (clickLook, F(pNavLook)),
            (5.3, F(pNavLook)),
            (clickDark, F(pDark)),
            (6.4, F(pDark)),
            (clickSwatch, F(pSwatch)),
            (7.8, F(pSwatch)),
            (clickSystem, F(pNavSystem)),
            (8.7, F(pNavSystem)),
            (clickOnTop, F(pOnTop)),
            (9.9, F(pOnTop)),
            (clickSave, F(pSave)),
            (10.6, F(pSave)),
            (11.3, new Point(W + 120, H * 0.85)),
        };
        var clicks = new[] { clickSound, clickLook, clickDark, clickSwatch, clickSystem, clickOnTop, clickSave };

        return new Clip("settings", 12.6, 1.05, t =>
        {
            Shot page;
            if (t < clickSound + 0.05)
            {
                double p = EaseInOut(Clamp01((t - dragStart) / (dragEnd - dragStart)));
                page = timer[(int)Math.Round(p * (timer.Count - 1))].shot;
            }
            else if (t < clickLook + 0.05) page = sound;
            else if (t < clickDark + 0.05) page = lookLight;
            else if (t < clickSwatch + 0.05) page = lookDark;
            else if (t < clickSystem + 0.05) page = lookAccent;
            else if (t < clickOnTop + 0.05) page = system;
            else page = systemOn;

            return new Frame
            {
                A = page, RectA = dialog,
                B = mainDark, RectB = window,
                Morph = EaseInOut(Clamp01((t - morphStart) / (morphEnd - morphStart))),
                Cursor = CursorAt(cursor, t),
                Pressed = clicks.Any(c => t >= c && t < c + ClickHold),
            };
        });
    }

    // ------------------------------------------------------------------ capturing

    /// <summary>Renders the window at 2x, the same way the site screenshots are made.</summary>
    private static Shot Capture(Window window)
    {
        window.UpdateLayout();
        Pump(100);
        int w = (int)Math.Round(window.ActualWidth * ShotScale);
        int h = (int)Math.Round(window.ActualHeight * ShotScale);
        var rtb = new RenderTargetBitmap(w, h, 96 * ShotScale, 96 * ShotScale, PixelFormats.Pbgra32);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var rect = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
            dc.DrawRectangle(window.Background, null, rect);
            var brush = new VisualBrush(window) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            dc.DrawRectangle(brush, null, rect);
        }
        rtb.Render(dv);
        rtb.Freeze();
        Console.WriteLine($"captured {w}x{h}");
        return new Shot(rtb, window.ActualWidth, window.ActualHeight);
    }

    /// <summary>Center of an element in window coordinates (logical pixels).</summary>
    private static Point CenterOf(Window window, FrameworkElement element) =>
        element.TransformToAncestor(window).Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));

    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
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

    /// <summary>Lets bindings, layout and the 200 ms engine tick settle.</summary>
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    // ------------------------------------------------------------------ composing

    private static void Render(Clip clip, string outFile, string ffmpeg, string? framesDir)
    {
        if (framesDir != null) Directory.CreateDirectory(framesDir);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);

        var psi = new ProcessStartInfo(ffmpeg)
        {
            Arguments = $"-y -f rawvideo -pix_fmt bgra -s {W}x{H} -r {Fps} -i - " +
                        "-c:v libx264 -pix_fmt yuv420p -crf 18 -preset slow -profile:v high -movflags +faststart " +
                        $"\"{outFile}\"",
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var ff = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        ff.ErrorDataReceived += (_, e) => { if (e.Data != null && e.Data.Contains("Error", StringComparison.OrdinalIgnoreCase)) Console.Error.WriteLine(e.Data); };
        ff.BeginErrorReadLine();
        var stdin = ff.StandardInput.BaseStream;

        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        var pixels = new byte[W * H * 4];
        int frames = (int)Math.Round(clip.Duration * Fps);
        var backdrop = Backdrop();

        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)Fps;
            double zoom = 1 + (clip.Zoom - 1) * SmoothStep(t / clip.Duration);
            var scene = BuildScene(clip.At(t), zoom, backdrop);
            scene.Measure(new Size(W, H));
            scene.Arrange(new Rect(0, 0, W, H));
            rtb.Clear();
            rtb.Render(scene);
            rtb.CopyPixels(pixels, W * 4, 0);
            stdin.Write(pixels, 0, pixels.Length);

            if (framesDir != null && i % (Fps / 2) == 0) SavePng(rtb, Path.Combine(framesDir, $"{clip.Name}_{i:0000}_{t:0.0}s.png"));
            if (i % Fps == 0) Console.WriteLine($"frame {i}/{frames}");
        }

        stdin.Flush();
        stdin.Close();
        ff.WaitForExit();
        if (ff.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exited with {ff.ExitCode}");
        Console.WriteLine($"{frames} frames -> {outFile}");
    }

    private static readonly Geometry CursorGeometry = Frozen(Geometry.Parse("M 0,0 L 0,17 L 4.2,13.2 L 7,19.5 L 9.6,18.4 L 6.9,12.2 L 12.4,12.2 Z"));

    /// <summary>One frame as a layout tree ready to render.</summary>
    private static FrameworkElement BuildScene(Frame f, double zoom, Brush backdrop)
    {
        var frame = new Canvas { Width = W, Height = H, Background = backdrop };
        // Everything the camera sees sits in "world"; the slow push-in scales it about the center.
        var world = new Canvas
        {
            Width = W, Height = H,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(zoom, zoom),
        };
        frame.Children.Add(world);

        var rect = Lerp(f.RectA, f.RectB, f.Morph);
        var window = new Grid
        {
            Width = rect.Width, Height = rect.Height,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(f.Pop, f.Pop),
        };
        Canvas.SetLeft(window, rect.X);
        Canvas.SetTop(window, rect.Y);
        double radius = 8 * rect.Width / (f.Morph < 0.5 ? f.A.W : f.B!.W); // Windows 11 corner radius at the drawn scale
        if (f.Morph < 1) window.Children.Add(WindowPane(f.A.Image, radius, 1 - f.Morph, shadow: true));
        if (f.Morph > 0) window.Children.Add(WindowPane(f.B!.Image, radius, f.Morph, shadow: f.Morph >= 1));
        world.Children.Add(window);

        if (f.Cursor is Point c)
        {
            double size = 2.3 * (f.Pressed ? 0.82 : 1);
            var cursor = new System.Windows.Shapes.Path
            {
                Data = CursorGeometry,
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 1.1,
                StrokeLineJoin = PenLineJoin.Round,
                RenderTransform = new ScaleTransform(size, size),
                Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 3, Opacity = 0.45, Direction = 270 },
            };
            Canvas.SetLeft(cursor, c.X);
            Canvas.SetTop(cursor, c.Y);
            world.Children.Add(cursor);
        }

        return frame;
    }

    /// <summary>The captured window in a rounded, outlined frame with a soft shadow.</summary>
    private static FrameworkElement WindowPane(BitmapSource shot, double radius, double opacity, bool shadow)
    {
        var pane = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = new ImageBrush(shot) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Top },
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Opacity = opacity,
        };
        RenderOptions.SetBitmapScalingMode(pane, BitmapScalingMode.HighQuality);
        if (shadow)
        {
            pane.Effect = new DropShadowEffect { BlurRadius = 90, ShadowDepth = 36, Direction = 270, Opacity = 0.38, Color = Color.FromRgb(0x20, 0x16, 0x1a) };
        }
        return pane;
    }

    /// <summary>Warm-to-cool gradient with soft blobs in the app's accent colors.</summary>
    private static Brush Backdrop()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var area = new Rect(0, 0, W, H);
            var baseBrush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0xff, 0xd6, 0xcf), 0),
                    new GradientStop(Color.FromRgb(0xf7, 0xee, 0xec), 0.5),
                    new GradientStop(Color.FromRgb(0xc9, 0xe6, 0xfb), 1),
                },
                new Point(0, 0), new Point(1, 1));
            dc.DrawRectangle(baseBrush, null, area);
            dc.DrawRectangle(Blob(Color.FromArgb(0x8c, 0xff, 0x6b, 0x6b), 0.18, 0.12, 0.55), null, area);
            dc.DrawRectangle(Blob(Color.FromArgb(0x80, 0x38, 0xbd, 0xf8), 0.86, 0.92, 0.6), null, area);
            dc.DrawRectangle(Blob(Color.FromArgb(0x50, 0xff, 0xb3, 0x6b), 0.95, 0.05, 0.4), null, area);
        }
        group.Freeze();
        return new DrawingBrush(group) { Stretch = Stretch.Fill };

        static RadialGradientBrush Blob(Color color, double cx, double cy, double r) => new(
            new GradientStopCollection
            {
                new GradientStop(color, 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            })
        {
            Center = new Point(cx, cy),
            GradientOrigin = new Point(cx, cy),
            RadiusX = r,
            RadiusY = r * W / H,
        };
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ------------------------------------------------------------------ geometry and easing

    /// <summary>A window of the given logical size drawn at a scale, centered in the frame.</summary>
    private static Rect Centered(double w, double h, double scale) =>
        new((W - w * scale) / 2, (H - h * scale) / 2, w * scale, h * scale);

    /// <summary>A point in window coordinates mapped into the frame, for a window drawn in rect at a scale.</summary>
    private static Point ToFrame(Rect rect, double scale, Point p) => new(rect.X + p.X * scale, rect.Y + p.Y * scale);

    /// <summary>Cursor position at time t: eased between keyframes, with a small arc on every move; null outside them.</summary>
    private static Point? CursorAt(List<(double t, Point p)> keys, double t)
    {
        if (t < keys[0].t || t >= keys[^1].t) return null;
        for (int i = 0; i < keys.Count - 1; i++)
        {
            var (t0, p0) = keys[i];
            var (t1, p1) = keys[i + 1];
            if (t < t0 || t >= t1) continue;
            double p = EaseInOut((t - t0) / (t1 - t0));
            double arc = p0 == p1 ? 0 : Math.Min(40, (p1 - p0).Length * 0.08) * Math.Sin(Math.PI * p);
            return new Point(Lerp(p0.X, p1.X, p), Lerp(p0.Y, p1.Y, p) - arc);
        }
        return null;
    }

    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
    private static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;
    private static double Lerp(double a, double b, double p) => a + (b - a) * p;
    private static Rect Lerp(Rect a, Rect b, double p) =>
        new(Lerp(a.X, b.X, p), Lerp(a.Y, b.Y, p), Lerp(a.Width, b.Width, p), Lerp(a.Height, b.Height, p));
    private static double SmoothStep(double p) { p = Clamp01(p); return p * p * (3 - 2 * p); }
    private static double EaseInOut(double p) => p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;
}
