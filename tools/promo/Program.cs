using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Path = System.IO.Path;
using PomodoroTimer.Services;
using PomodoroTimer.ViewModels;
using PomodoroTimer.Views;

namespace Promo;

/// <summary>
/// Renders the promo clip. The app has no continuous animations of its own (the clock steps
/// once a second, the ring once per tick), so the window is captured in a handful of states
/// and every frame of the clip is composed offline: gradient backdrop, shadow, a slow camera
/// push-in, a drawn cursor that switches the window to compact mode. ffmpeg encodes the frames
/// streamed through stdin.
/// </summary>
internal static class Program
{
    // Output frame: 4:5 portrait like the reference clip, H.264-friendly even size.
    private const int W = 1080, H = 1350, Fps = 30;
    private const double Duration = 9.4;

    // The window is captured at 2x and placed 1:1 at camera scale 1, so it stays crisp.
    private const double Shot = 2;
    private const double BigW = 340, BigH = 540, CompactW = 250, CompactH = 300;

    // Timeline (seconds). The clock changes at x.9 so no change falls inside the morph.
    private const double BreakAt = 3.9;                       // focus ends, break starts
    private const double CursorIn = 4.6, CursorAt = 5.7;        // cursor flies to the compact button
    private const double PressAt = 5.85, PressEnd = 6.0;        // click
    private const double MorphStart = 6.0, MorphEnd = 6.5;      // window turns compact
    private const double CursorOut = 6.8, CursorGone = 7.6;     // cursor leaves

    [STAThread]
    private static int Main(string[] args)
    {
        string outFile = "promo.mp4";
        string ffmpeg = "ffmpeg";
        string? framesDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-ffmpeg": ffmpeg = args[++i]; break;
                case "-frames": framesDir = args[++i]; break;
                default: outFile = args[i]; break;
            }
        }

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
        s.LimitSessions = true;
        s.TotalSessions = 4;
        s.AutoStartBreaks = true;
        s.CompactMode = false;
        s.AlwaysOnTop = false;
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

        var shots = CaptureShots(vm, main, out Point compactButton);
        vm.Dispose();

        Render(shots, compactButton, outFile, ffmpeg, framesDir);
        Console.WriteLine($"done -> {Path.GetFullPath(outFile)}");
        return 0;
    }

    // ------------------------------------------------------------------ capturing the app

    /// <summary>
    /// Focus at 00:04 … 00:01, then the countdown completes by itself and the break auto-starts
    /// (05:00 …), then compact mode. Keys: w4..w1, b300.., c298..
    /// </summary>
    private static Dictionary<string, BitmapSource> CaptureShots(MainViewModel vm, MainWindow main, out Point compactButton)
    {
        var shots = new Dictionary<string, BitmapSource>();
        var engine = GetEngine(vm);

        vm.ToggleCommand.Execute(null); // start focus, session 2 of 4
        for (int sec = 4; sec >= 1; sec--)
        {
            SetState(engine, completed: 1, remaining: TimeSpan.FromSeconds(sec - 0.1));
            Pump(250);
            shots[$"w{sec}"] = Capture(main);
        }

        compactButton = CenterOf(main, FindButton(main, main.Resources["CompactButton"] as Style));

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

        return shots;
    }

    /// <summary>The button carrying a given style (the compact toggle has no name in the XAML).</summary>
    private static Button FindButton(DependencyObject root, Style? style) =>
        FindButtonOrNull(root, style ?? throw new InvalidOperationException("style not found"))
        ?? throw new InvalidOperationException("compact button not found");

    private static Button? FindButtonOrNull(DependencyObject node, Style style)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is Button b && b.Style == style) return b;
            var found = FindButtonOrNull(child, style);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Center of an element in window coordinates (logical pixels).</summary>
    private static Point CenterOf(Window window, FrameworkElement element) =>
        element.TransformToAncestor(window).Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));

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

    /// <summary>Renders the window at 2x, the same way the site screenshots are made.</summary>
    private static BitmapSource Capture(Window window)
    {
        window.UpdateLayout();
        Pump(100);
        int w = (int)Math.Round(window.ActualWidth * Shot);
        int h = (int)Math.Round(window.ActualHeight * Shot);
        var rtb = new RenderTargetBitmap(w, h, 96 * Shot, 96 * Shot, PixelFormats.Pbgra32);

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
        return rtb;
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

    // ------------------------------------------------------------------ composing the clip

    private static void Render(Dictionary<string, BitmapSource> shots, Point compactButton, string outFile, string ffmpeg, string? framesDir)
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
        int frames = (int)Math.Round(Duration * Fps);
        var cursorGeometry = Geometry.Parse("M 0,0 L 0,17 L 4.2,13.2 L 7,19.5 L 9.6,18.4 L 6.9,12.2 L 12.4,12.2 Z");
        cursorGeometry.Freeze();

        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)Fps;
            var scene = BuildScene(shots, compactButton, cursorGeometry, t);
            scene.Measure(new Size(W, H));
            scene.Arrange(new Rect(0, 0, W, H));
            rtb.Clear();
            rtb.Render(scene);
            rtb.CopyPixels(pixels, W * 4, 0);
            stdin.Write(pixels, 0, pixels.Length);

            if (framesDir != null && i % (Fps / 2) == 0) SavePng(rtb, Path.Combine(framesDir, $"f{i:0000}_{t:0.0}s.png"));
            if (i % Fps == 0) Console.WriteLine($"frame {i}/{frames}");
        }

        stdin.Flush();
        stdin.Close();
        ff.WaitForExit();
        if (ff.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exited with {ff.ExitCode}");
        Console.WriteLine($"{frames} frames -> {outFile}");
    }

    /// <summary>One frame of the clip at time t, as a layout tree ready to render.</summary>
    private static FrameworkElement BuildScene(Dictionary<string, BitmapSource> shots, Point compactButton, Geometry cursorGeometry, double t)
    {
        var frame = new Canvas { Width = W, Height = H, Background = Backdrop() };
        // Everything the camera sees sits in "world"; the slow push-in scales it about the center.
        double zoom = 1 + 0.07 * SmoothStep(t / Duration);
        var world = new Canvas
        {
            Width = W, Height = H,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(zoom, zoom),
        };
        frame.Children.Add(world);

        // ---- the window
        var big = new Rect((W - BigW * Shot) / 2, (H - BigH * Shot) / 2, BigW * Shot, BigH * Shot);
        var compact = new Rect((W - CompactW * Shot) / 2, (H - CompactH * Shot) / 2, CompactW * Shot, CompactH * Shot);
        double morph = EaseInOut(Clamp01((t - MorphStart) / (MorphEnd - MorphStart)));
        var rect = Lerp(big, compact, morph);

        // A small bounce when the phase ends: the window "pops" like it does on a real desktop.
        double pop = 1 + 0.035 * Math.Sin(Math.PI * Clamp01((t - BreakAt) / 0.45));

        var window = new Grid
        {
            Width = rect.Width, Height = rect.Height,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(pop, pop),
        };
        Canvas.SetLeft(window, rect.X);
        Canvas.SetTop(window, rect.Y);

        string bigKey = t < 0.9 ? "w4" : t < 1.9 ? "w3" : t < 2.9 ? "w2" : t < 3.9 ? "w1" : t < 4.9 ? "b300" : t < 5.9 ? "b299" : "b298";
        string compactKey = t < 6.9 ? "c298" : t < 7.9 ? "c297" : t < 8.9 ? "c296" : "c295";

        double radius = 8 * Shot; // Windows 11 corner radius at 2x
        if (morph < 1) window.Children.Add(WindowPane(shots[bigKey], radius, 1 - morph, shadow: true));
        if (morph > 0) window.Children.Add(WindowPane(shots[compactKey], radius, morph, shadow: morph >= 1));
        world.Children.Add(window);

        // ---- the cursor: aims at the compact button of the big window and stays put while the window shrinks
        var target = new Point(big.X + compactButton.X * Shot, big.Y + compactButton.Y * Shot);
        var start = new Point(W + 80, H + 80);
        var exit = new Point(W + 120, H * 0.78);
        Point? pos = null;
        if (t >= CursorIn && t < CursorOut)
        {
            double p = EaseInOut(Clamp01((t - CursorIn) / (CursorAt - CursorIn)));
            pos = new Point(Lerp(start.X, target.X, p), Lerp(start.Y, target.Y, p) - 90 * Math.Sin(Math.PI * p));
        }
        else if (t >= CursorOut && t < CursorGone)
        {
            double p = EaseIn(Clamp01((t - CursorOut) / (CursorGone - CursorOut)));
            pos = new Point(Lerp(target.X, exit.X, p), Lerp(target.Y, exit.Y, p));
        }
        if (pos is Point c)
        {
            double press = t >= PressAt && t < PressEnd ? 0.82 : 1;
            double size = 2.3 * press;
            var cursor = new System.Windows.Shapes.Path
            {
                Data = cursorGeometry,
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

    /// <summary>Warm-to-cool gradient with two soft blobs in the app's accent colors.</summary>
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

    // ------------------------------------------------------------------ easing helpers

    private static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;
    private static double Lerp(double a, double b, double p) => a + (b - a) * p;
    private static Rect Lerp(Rect a, Rect b, double p) =>
        new(Lerp(a.X, b.X, p), Lerp(a.Y, b.Y, p), Lerp(a.Width, b.Width, p), Lerp(a.Height, b.Height, p));
    private static double SmoothStep(double p) { p = Clamp01(p); return p * p * (3 - 2 * p); }
    private static double EaseInOut(double p) => p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;
    private static double EaseIn(double p) => p * p * p;
}
