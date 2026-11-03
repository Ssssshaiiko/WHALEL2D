using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FishPet;

// The pet: a transparent topmost window with her in it, her speech bubble above her head, her reflexes when
// clicked, dragged or left alone (Behavior), her own body language from what the agent has her say (Body,
// Line), and her mirror. Everything the agent does goes through Execute (Tools.cs for what each tool means).
internal sealed class PetWindow : Window
{
    private readonly Settings settings;
    private readonly string? model;
    private readonly bool test;
    private readonly SpriteImage image = new() { Stretch = Stretch.Fill };
    private readonly BubbleWindow bubble;
    private readonly Speech speech;
    private readonly Behavior behavior;
    private readonly Body body;
    private readonly DispatcherTimer timer;
    // Out of the way while another program fills the screen (settings: hide_on_fullscreen), looked at twice a second.
    private bool hiddenForFullscreen;
    private DateTime nextFullscreenLook;
    private Player? player;
    private ControlServer? server;
    private HttpApi? api;
    private string? trouble;
    private bool exiting, exitReady;
    private long shapeChange;
    private Shape Shape => Shape.Find(settings.Shape);

    // Dragging her about.
    private bool dragging, dragged;
    private Point dragStart, windowStart;

    // What the window does to her: moving pushes her body, the taskbar hides what reaches it.
    private double lastLeft, lastTop, lookX = 9, lookY = 9;
    private Rect open;

    // Her lines: one at a time, in order; a line of nothing but 〔〕 plays with no bubble.
    private Task lines = Task.CompletedTask;
    private Line? silent;
    private readonly Stopwatch silentClock = new();
    private DateTime? relaxAt;
    private const double LingerSeconds = 3.5;

    // Her recorded voice, and her mouth.
    private readonly MediaPlayer voicePlayer = new();
    private float[]? voice;
    private DateTime voiceUntil;
    private double mouth;
    private const int VoiceStepMilliseconds = 33;

    // Every line she says is filmed, for looking back at afterwards; kept half an hour.
    private sealed record Photo(DateTime At, string Said, string Picture);
    private readonly List<Photo> photos = [];
    private List<BitmapSource>? film;
    private string filmSaid = "";
    private DateTime filmShot;
    private DateTime? filmEnded;
    private const int PhotosKept = 20, FilmFrames = 120;
    private static readonly TimeSpan PhotoLife = TimeSpan.FromMinutes(30), FilmEvery = TimeSpan.FromMilliseconds(150), FilmAfter = TimeSpan.FromMilliseconds(600);
    private const int MirrorHeight = 520, MirrorGridHeight = 360, MirrorShots = 6, MirrorColumns = 3;
    private const double MirrorSpan = .5;

    // What the user did to her, for the agent (recent_events).
    private sealed record Happening(long Sequence, DateTime At, string Kind, object Detail);
    private readonly List<Happening> happenings = [];
    private long sequence;

    internal PetWindow(Settings settings, string? model, bool test, string? pipe = null)
    {
        this.settings = settings; this.model = model; this.test = test;
        Title = "大肥鱼";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.Opacity = 0;
        Content = new Grid { Children = { image } };
        bubble = new BubbleWindow(this, () => Height * Shape.HeadTop);
        bubble.Quiet = test;
        bubble.Scale = settings.BubbleScale;
        speech = new Speech(bubble) { Seconds = settings.BubbleSeconds };
        body = new Body(Send);
        behavior = new Behavior(Send, () => [.. settings.Outfit, .. body.Overlays], new Random()) { HandBusy = () => body.HandUp, Ended = body.Restore };
        ApplySize();
        Place();
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Never takes the focus from what the user is doing, not in Alt+Tab.
            SetWindowLongPtr(handle, -20, (nint)((long)GetWindowLongPtr(handle, -20) | 0x8000080));
        };
        Loaded += (_, _) =>
        {
            Start();
            server = new ControlServer(Execute, pipe);
            try { api = HttpApi.Start(Places.ApiFile, test ? 0 : HttpApi.DefaultPort, Execute); }
            catch (Exception error) { Places.Log("网页接口开不起来：" + error.Message); }
        };
        PreviewMouseLeftButtonDown += Down;
        PreviewMouseMove += Move;
        PreviewMouseLeftButtonUp += Up;
        LostMouseCapture += (_, _) => { if (dragging) { dragging = false; if (dragged) Dropped(); } };
        Closing += Leaving;
        Closed += (_, _) => { server?.Dispose(); api?.Dispose(); player?.Dispose(); bubble.Close(); };
    }

    private void Send(string command) => player?.Send(command);

    // ---- Her renderer ----

    private void Start()
    {
        if (model == null) { Trouble("没找到模型：把模型文件夹放进插件的 model 文件夹里（里面要有 " + Places.ModelFile + "）。"); return; }
        if (!File.Exists(Places.Renderer)) { Trouble("没找到渲染程序：先运行插件里的 setup.ps1 编译它。"); return; }
        var started = new Player(image);
        started.Ready += Ready;
        started.Failed += message => Trouble("渲染出问题了：" + message);
        player = started;
        lastLeft = Left; lastTop = Top;
        var dpi = VisualTreeHelper.GetDpi(this);
        started.Start(model, Shape, settings.Outfit, (int)(Width * dpi.DpiScaleX), (int)(Height * dpi.DpiScaleY), settings.Fps);
        body.Forget(); silent = null; relaxAt = null;
        behavior.Resume(DateTime.UtcNow, settings.IdleSeconds);
        timer.Start();
        open = default;
        UpdateCut();
    }

    private void Trouble(string message)
    {
        trouble = message;
        Places.Log(message);
        bubble.Say(message);
    }

    // She comes in and greets once her body type has settled.
    private void Ready()
    {
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        behavior.Show("问候", DateTime.UtcNow);
    }

    private void ApplySize()
    {
        var shape = Shape;
        // Her feet stay where they are, not the window's corner, so resizing does not lift her out of the taskbar.
        double middle = Left + Width / 2, feet = Top + Height * shape.FootAt;
        Width = Math.Round(shape.Width * settings.Scale);
        Height = Math.Round(shape.Height * settings.Scale);
        if (double.IsFinite(middle) && double.IsFinite(feet)) { Left = middle - Width / 2; Top = feet - Height * shape.FootAt; }
        if (PresentationSource.FromVisual(this) != null)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Send($"resize {(int)(Width * dpi.DpiScaleX)} {(int)(Height * dpi.DpiScaleY)}");
        }
        UpdateCut();
    }

    // Where she was, or the bottom right corner half sunk behind the taskbar. In a test, out of sight.
    private void Place()
    {
        if (test) { Left = -6000; Top = 100; return; }
        var area = SystemParameters.WorkArea;
        Left = settings.Left ?? area.Right - Width - 8;
        Top = settings.Top ?? area.Bottom - Height * .5;
        Clamp();
    }

    // She may sink behind the taskbar, but her face stays out.
    private void Clamp()
    {
        if (test) return;
        var area = SystemParameters.WorkArea;
        var shape = Shape;
        var bounds = new Rect(Width * shape.Side, Height * shape.HeadTop, Width * (1 - 2 * shape.Side), Height * (shape.HeadAt + .12 - shape.HeadTop));
        Left = Math.Clamp(Left, area.Left - bounds.Left, Math.Max(area.Left - bounds.Left, area.Right - bounds.Right));
        Top = Math.Clamp(Top, area.Top - bounds.Top, Math.Max(area.Top - bounds.Top, area.Bottom - bounds.Bottom));
    }

    private void SavePlace()
    {
        if (test) return;
        settings.Left = Left; settings.Top = Top;
        settings.Save();
    }

    // A topmost window draws over the taskbar: whatever of her reaches it is not drawn, and clicks there
    // pass through, so she stands behind it.
    private void UpdateCut()
    {
        if (!IsVisible || test) return;
        var work = SystemParameters.WorkArea;
        var monitor = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        var window = new Rect(Left, Top, Width, Height);
        double left = work.Left > monitor.Left ? Math.Clamp(work.Left - window.Left, 0, window.Width) : 0;
        double top = work.Top > monitor.Top ? Math.Clamp(work.Top - window.Top, 0, window.Height) : 0;
        double right = work.Right < monitor.Right ? Math.Clamp(work.Right - window.Left, left, window.Width) : window.Width;
        double bottom = work.Bottom < monitor.Bottom ? Math.Clamp(work.Bottom - window.Top, top, window.Height) : window.Height;
        var uncovered = new Rect(left, top, right - left, bottom - top);
        if (uncovered == open) return;
        open = uncovered;
        image.Clip = uncovered == new Rect(0, 0, Width, Height) ? null : new RectangleGeometry(uncovered);
    }

    // Her body takes a moment to change while the window changes at once: she goes out, changes unseen, and comes back.
    private async void SetShape(string id)
    {
        settings.Shape = Shape.Find(id).Id;
        settings.Save();
        if (player is not { } current) return;
        long change = ++shapeChange;
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
        await Task.Delay(130);
        if (change != shapeChange) return;
        ApplySize();
        current.SetShape(Shape);
        Clamp();
        long seen = current.Frames;
        for (int wait = 0; wait < 40 && current.Frames < seen + 3; wait++) await Task.Delay(20);
        if (change == shapeChange) image.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
    }

    private void Tick()
    {
        if (player is not { } current) return;
        if (DateTime.UtcNow >= nextFullscreenLook)
        {
            nextFullscreenLook = DateTime.UtcNow.AddMilliseconds(500);
            UpdateFullscreen(current);
        }
        if (hiddenForFullscreen) return;
        // Moving the window pushes her body; physics carries that into her hair, skirt and tail.
        double swayX = (Left - lastLeft) * 30, swayY = (Top - lastTop) * 30;
        lastLeft = Left; lastTop = Top;
        if (swayX != 0 || swayY != 0) current.Send(FormattableString.Invariant($"sway {swayX:0} {swayY:0}"));
        UpdateCut();
        UpdateMouth(current);
        if ((server?.Connections ?? 0) == 0) behavior.ClearActivities();   // Nobody is left to say the work ended.
        UpdateBody();
        UpdateFilm();
        speech.Tick();
        // Nothing of her own while she says something or still holds the look she said it with.
        behavior.Tick(DateTime.UtcNow, voice != null || speech.IsSaying || silent != null || body.Posing, settings.IdleSeconds);
        if (!settings.FollowMouse || test)
        {
            if (lookX == 0 && lookY == 0) return;
            lookX = lookY = 0;
            current.Send("look 0 0");
            return;
        }
        GetCursorPos(out var cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        double headX = (Left + Width / 2) * dpi.DpiScaleX, headY = (Top + Height * Shape.HeadAt) * dpi.DpiScaleY;
        double x = Math.Clamp((cursor.X - headX) / 500, -1, 1), y = Math.Clamp((headY - cursor.Y) / 400, -1, 1);
        if (Math.Abs(x - lookX) + Math.Abs(y - lookY) < .01) return;
        lookX = x; lookY = y;
        current.Send(FormattableString.Invariant($"look {x:0.00} {y:0.00}"));
    }

    // While another program fills the screen she is hidden and her renderer draws nothing; her words still play on,
    // unseen, and she is back on top once it is gone. Not in a test, which is out of sight anyway.
    private void UpdateFullscreen(Player current)
    {
        bool covered = settings.HideOnFullscreen && !test && Fullscreen.InFront();
        if (covered == hiddenForFullscreen) return;
        hiddenForFullscreen = covered;
        current.Send($"pause {(covered ? 1 : 0)}");
        bubble.Quiet = covered || test;
        if (covered) { bubble.Hide(); Hide(); return; }
        Show();
        Topmost = false; Topmost = true;
    }

    // ---- The user's hands on her ----

    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (exiting || e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        dragging = true; dragged = false;
        GetCursorPos(out var p);
        dragStart = new Point(p.X, p.Y);
        windowStart = new Point(Left, Top);
        CaptureMouse();
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
        GetCursorPos(out var p);
        var dpi = VisualTreeHelper.GetDpi(this);
        double dx = (p.X - dragStart.X) / dpi.DpiScaleX, dy = (p.Y - dragStart.Y) / dpi.DpiScaleY;
        if (Math.Abs(dx) + Math.Abs(dy) > 4 && !dragged)
        {
            dragged = true;
            behavior.DragStart();
            Happened("drag_start", new { });
        }
        if (dragged) { Left = windowStart.X + dx; Top = windowStart.Y + dy; }
        e.Handled = true;
    }

    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        e.Handled = true;
        dragging = false;
        ReleaseMouseCapture();
        if (dragged) { Dropped(); return; }
        // The window also gets clicks on her faint edges, which count as missing her.
        var at = e.GetPosition(this);
        if (VisualTreeHelper.HitTest(image, at) == null) return;
        var zone = Behavior.Zone(Shape.Id, at.X / Width, at.Y / Height, settings.Outfit);
        behavior.Click(zone, DateTime.UtcNow, at.X / Width < .5 ? 1 : -1);
        Happened("click", new { zone, reaction = behavior.Showing });
    }

    private void Dropped()
    {
        Clamp();
        SavePlace();
        behavior.DragEnd(DateTime.UtcNow);
        Happened("drag_end", new { });
    }

    private void Happened(string kind, object detail)
    {
        var happening = new Happening(++sequence, DateTime.UtcNow, kind, detail);
        happenings.Add(happening);
        if (happenings.Count > 100) happenings.RemoveAt(0);
        server?.Publish(kind, detail);
    }

    // ---- What she says ----

    // A new line waits for the one before to be played out, or her body would be cut off mid-move.
    private async Task WaitForLine(CancellationToken token)
    {
        var waited = Stopwatch.StartNew();
        while ((silent != null || speech.IsSaying) && waited.Elapsed.TotalSeconds < 20) await Task.Delay(50, token);
    }

    // In the order they came, each once the one before has been acted out.
    private async Task InTurn(Func<Task> act, CancellationToken token)
    {
        var turn = new TaskCompletionSource();
        var before = lines;
        lines = turn.Task;
        try { await before; await WaitForLine(token); await act(); }
        finally { turn.SetResult(); }
    }

    private void SayLine(string text, double? voiceSeconds)
    {
        relaxAt = null;
        var line = new Line(text, body.Apply, voiceSeconds);
        if (line.Plain.Trim().Length == 0) { PlaySilently(text); return; }
        StartFilming(line.Plain);
        speech.Say(line);
    }

    // film: false for what she tries in the mirror, which she sees there already.
    private void PlaySilently(string text, bool filmed = true)
    {
        relaxAt = null;
        silent = new Line(text, body.Apply);
        if (filmed) StartFilming(silent.Plain);
        silentClock.Restart();
        silent.Advance(0);
    }

    // Her own look stays a while after her words, then she relaxes; not in the middle of one of her reflexes.
    private void UpdateBody()
    {
        if (silent is { } line)
        {
            line.Advance(silentClock.Elapsed.TotalSeconds);
            if (line.Done) silent = null;
        }
        if (!body.Posing || silent != null || speech.IsSaying) { relaxAt = null; return; }
        relaxAt ??= DateTime.UtcNow.AddSeconds(LingerSeconds);
        if (DateTime.UtcNow < relaxAt || behavior.Showing != null) return;
        body.Relax();
        relaxAt = null;
    }

    // Returns how long the recording is, for spreading what she does over it.
    private double? PlayVoice(string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || !Path.IsPathRooted(file) || !file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            throw new ArgumentException("audio 要是本机 wav 文件的完整路径，而且文件要在。");
        voicePlayer.Stop();
        voicePlayer.Open(new Uri(file));
        voicePlayer.Play();
        try { voice = Behavior.VoiceLevels(File.ReadAllBytes(file), VoiceStepMilliseconds); }
        catch (IOException) { voice = null; }
        // A voice that never starts playing must not hold her mouth for ever.
        if (voice != null) voiceUntil = DateTime.UtcNow.AddMilliseconds(voice.Length * VoiceStepMilliseconds + 2000);
        return voice?.Length * VoiceStepMilliseconds / 1000.0;
    }

    // Her recorded voice when there is one; otherwise her mouth moves while the words come out.
    private void UpdateMouth(Player current)
    {
        double level = 0;
        if (voice is { } levels)
        {
            int step = (int)(voicePlayer.Position.TotalMilliseconds / VoiceStepMilliseconds);
            if (step < levels.Length && DateTime.UtcNow < voiceUntil) level = levels[step];
            else voice = null;
        }
        if (voice == null && speech.IsTalking) level = .2 + .35 * Math.Abs(Math.Sin(Environment.TickCount64 / 70.0));
        if (level == mouth) return;
        mouth = level;
        current.Send(FormattableString.Invariant($"mouth {level:0.00}"));
    }

    // ---- Her mirror and her photos ----

    // What she looks like right now, head to waist so her face is big enough to read, on white.
    private BitmapSource? Snapshot(int most = MirrorHeight)
    {
        if (image.Source is not BitmapSource frame || frame.PixelWidth == 0) return null;
        int top = (int)(frame.PixelHeight * Math.Max(0, Shape.HeadTop - .03)), span = Math.Min(frame.PixelHeight - top, (int)(frame.PixelHeight * MirrorSpan));
        var upper = new CroppedBitmap(frame, new Int32Rect(0, top, frame.PixelWidth, span));
        double scale = Math.Min(1, most / (double)span);
        int width = (int)Math.Round(frame.PixelWidth * scale), height = (int)Math.Round(span * scale);
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            draw.DrawImage(upper, new Rect(0, 0, width, height));
        }
        var flat = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        flat.Render(visual);
        flat.Freeze();
        return flat;
    }

    // Looks in rows of three, left to right and down in the order they were seen, as a PNG data URL.
    private static string Picture(IReadOnlyList<BitmapSource> shots)
    {
        int columns = Math.Min(MirrorColumns, shots.Count), rows = (shots.Count + columns - 1) / columns;
        int cellWidth = shots.Max(shot => shot.PixelWidth), cellHeight = shots.Max(shot => shot.PixelHeight);
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, cellWidth * columns, cellHeight * rows));
            for (int i = 0; i < shots.Count; i++)
                draw.DrawImage(shots[i], new Rect(i % columns * cellWidth, i / columns * cellHeight, shots[i].PixelWidth, shots[i].PixelHeight));
        }
        var strip = new RenderTargetBitmap(cellWidth * columns, cellHeight * rows, 96, 96, PixelFormats.Pbgra32);
        strip.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(strip));
        using var bytes = new MemoryStream();
        encoder.Save(bytes);
        return "data:image/png;base64," + Convert.ToBase64String(bytes.ToArray());
    }

    private static IReadOnlyList<BitmapSource> Moments(IReadOnlyList<BitmapSource> seen) => seen.Count <= MirrorShots ? seen
        : Enumerable.Range(0, MirrorShots).Select(i => seen[(int)Math.Round(i * (seen.Count - 1) / (double)(MirrorShots - 1))]).ToList();

    // She tries something in front of the mirror: done for real, watched all through, six moments given back.
    private async Task<IReadOnlyList<BitmapSource>> TryInMirror(string pose, CancellationToken token)
    {
        var seen = new List<BitmapSource>();
        await InTurn(async () =>
        {
            PlaySilently(pose.Contains('〔') ? pose : "〔" + pose + "〕", filmed: false);
            var line = silent!;
            var clock = Stopwatch.StartNew();
            // Until it has all played and settled a moment, never longer than six seconds.
            while (clock.Elapsed.TotalSeconds < 6 && !(line.Done && clock.Elapsed.TotalSeconds > line.Until + .3))
            {
                if (Snapshot(MirrorGridHeight) is { } shot) seen.Add(shot);
                await Task.Delay(120, token);
            }
        }, token);
        return Moments(seen);
    }

    private void StartFilming(string said)
    {
        FinishFilming();
        film = []; filmSaid = said; filmEnded = null; filmShot = default;
    }

    // A look every FilmEvery while she says it and a moment after; too long a line keeps every other one.
    private void UpdateFilm()
    {
        if (film is not { } shots) return;
        var now = DateTime.UtcNow;
        if (speech.IsSaying || silent != null) filmEnded = null; else filmEnded ??= now;
        if (now - filmShot >= FilmEvery && Snapshot(MirrorGridHeight) is { } shot)
        {
            shots.Add(shot); filmShot = now;
            if (shots.Count >= FilmFrames) { var kept = shots.Where((_, i) => i % 2 == 0).ToList(); shots.Clear(); shots.AddRange(kept); }
        }
        if (filmEnded is { } ended && now - ended >= FilmAfter) FinishFilming();
    }

    private void FinishFilming()
    {
        var shots = film;
        film = null;
        if (shots is not { Count: > 0 }) return;
        photos.Add(new(DateTime.UtcNow, filmSaid, Picture(Moments(shots))));
        TrimPhotos();
    }

    private void TrimPhotos()
    {
        photos.RemoveAll(photo => DateTime.UtcNow - photo.At > PhotoLife);
        if (photos.Count > PhotosKept) photos.RemoveRange(0, photos.Count - PhotosKept);
    }

    // ---- Leaving ----

    // She bows and fades out while still bowed, rather than vanishing halfway down.
    private async void Leaving(object? sender, CancelEventArgs e)
    {
        if (exitReady) return;
        e.Cancel = true;
        if (exiting) return;
        exiting = true;
        speech.Stop();
        if (player != null && image.Opacity > 0)
        {
            behavior.Show("告别", DateTime.UtcNow);
            await Task.Delay(1600);
            image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(500)));
            await Task.Delay(600);
        }
        exitReady = true;
        Close();
        Application.Current.Shutdown();
    }

    // ---- The agent's tools ----

    private Task<object> Execute(string name, JsonElement args, CancellationToken token) =>
        Dispatcher.InvokeAsync(() => Run(name, args, token)).Task.Unwrap();

    private async Task<object> Run(string name, JsonElement args, CancellationToken token)
    {
        if (exiting) throw new InvalidOperationException("你正在告别，收起之后再叫你出来。");
        if (name is not ("get_state" or "quit" or "recent_events") && trouble != null && player == null) throw new InvalidOperationException(trouble);
        switch (name)
        {
            case "say":
                var text = Tools.RequiredText(args, "text");
                var audio = Tools.OptionalText(args, "audio");
                // A line each, in turn; the call returns once the first has begun, the rest wait their turn whoever
                // asked. A recording is one utterance, so with one it is a single bubble.
                var bubbles = audio != null ? [text] : Line.Split(text);
                await InTurn(() => { SayLine(bubbles[0], audio != null ? PlayVoice(audio) : null); return Task.CompletedTask; }, token);
                foreach (var next in bubbles.Skip(1))
                    _ = InTurn(() => { if (!exiting) SayLine(next, null); return Task.CompletedTask; }, CancellationToken.None);
                return Tools.Text(new { started = true, bubbles = bubbles.Count, said = Body.Strip(text) });
            case "look_in_mirror":
                var pose = Tools.OptionalText(args, "try");
                IReadOnlyList<BitmapSource> shots = pose != null ? await TryInMirror(pose, token) : Snapshot() is { } now ? [now] : [];
                if (shots.Count == 0) throw new InvalidOperationException("镜子里还什么都看不到，等你出现了再照。");
                return Tools.Image(Picture(shots), new { shots = shots.Count, tried = pose });
            case "look_back":
                int which = args.TryGetProperty("which", out var value) && value.TryGetInt32(out var number) ? number : 1;
                if (film != null && !speech.IsSaying && silent == null) FinishFilming();
                TrimPhotos();
                if (photos.Count == 0) throw new InvalidOperationException("这半小时里还没有拍下你说话的样子。");
                if (which < 1 || which > photos.Count) throw new ArgumentException("只留着最近 " + photos.Count + " 句的样子。");
                var photo = photos[^which];
                int Ago(DateTime at) => (int)(DateTime.UtcNow - at).TotalSeconds;
                return Tools.Image(photo.Picture, new { said = photo.Said, secondsAgo = Ago(photo.At),
                    recent = Enumerable.Range(1, photos.Count).Select(n => new { which = n, said = photos[^n].Said, secondsAgo = Ago(photos[^n].At) }).ToArray() });
            case "set_activity":
                var kind = Tools.RequiredText(args, "kind", 40);
                if (!args.TryGetProperty("active", out var active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("要给 active（true 或 false）。");
                behavior.Activity(kind, active.GetBoolean());
                return Tools.Text(new { kind, active = active.GetBoolean() });
            case "dress":
                foreach (var label in Tools.Texts(args, "wear").Concat(Tools.Texts(args, "remove")))
                    if (Outfit.Expression(label) == null) throw new ArgumentException("没有“" + label + "”。衣服和配饰见 dress 的说明。");
                foreach (var label in Tools.Texts(args, "wear")) Dress(Outfit.Expression(label)!, true);
                foreach (var label in Tools.Texts(args, "remove")) Dress(Outfit.Expression(label)!, false);
                return Tools.Text(new { wearing = Wearing() });
            case "get_state":
                return Tools.Text(State());
            case "update_settings":
                if (args.TryGetProperty("fps", out var fps) && fps.TryGetInt32(out var rate))
                {
                    if (!Settings.FpsChoices.Contains(rate)) throw new ArgumentException("fps 只能是 30、60、90、120。");
                    settings.Fps = rate; Send($"fps {rate}");
                }
                if (args.TryGetProperty("scale", out var scale) && scale.TryGetDouble(out var size))
                {
                    settings.Scale = Math.Clamp(size, .5, 2); ApplySize(); Clamp();
                }
                if (args.TryGetProperty("bubble_scale", out var bubbleScale) && bubbleScale.TryGetDouble(out var bubbleSize))
                {
                    settings.BubbleScale = Math.Clamp(bubbleSize, .5, 2); bubble.Scale = settings.BubbleScale;
                }
                if (args.TryGetProperty("bubble_seconds", out var stay) && stay.TryGetInt32(out var stays))
                {
                    settings.BubbleSeconds = Settings.StayFor(stays); speech.Seconds = settings.BubbleSeconds;
                }
                // Taken up at once: hidden behind a fullscreen program she comes back if it is turned off.
                if (args.TryGetProperty("hide_on_fullscreen", out var hide) && hide.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings.HideOnFullscreen = hide.GetBoolean(); nextFullscreenLook = default;
                }
                if (args.TryGetProperty("follow_mouse", out var follow) && follow.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.FollowMouse = follow.GetBoolean();
                if (args.TryGetProperty("idle_seconds", out var idle) && idle.TryGetInt32(out var seconds)) settings.IdleSeconds = Math.Clamp(seconds, 15, 600);
                settings.Save();
                if (args.TryGetProperty("shape", out var shape) && shape.ValueKind == JsonValueKind.String) SetShape(shape.GetString()!);
                return Tools.Text(State());
            case "recent_events":
                long after = args.TryGetProperty("after", out var since) && since.TryGetInt64(out var mark) ? mark : 0;
                return Tools.Text(happenings.Where(item => item.Sequence > after)
                    .Select(item => new { sequence = item.Sequence, secondsAgo = (int)(DateTime.UtcNow - item.At).TotalSeconds, kind = item.Kind, detail = item.Detail }).ToArray());
            case "quit":
                _ = Dispatcher.BeginInvoke(new Action(Close));
                return Tools.Text(new { leaving = true });
            default:
                throw new ArgumentException("没有这个工具。");
        }
    }

    private void Dress(string expression, bool worn)
    {
        settings.Outfit = worn ? settings.Outfit.Append(expression).Distinct().ToArray() : settings.Outfit.Where(item => item != expression).ToArray();
        Send($"exp {expression} {(worn ? 1 : 0)}");
        behavior.UpdateGlasses();
        settings.Save();
    }

    private string[] Wearing() => Outfit.Clothes.Concat(Outfit.Accessories).Where(item => settings.Outfit.Contains(item.Expression)).Select(item => item.Label).ToArray();

    private object State() => new
    {
        shape = settings.Shape, shapeLabel = Shape.Label, wearing = Wearing(), fps = settings.Fps, scale = settings.Scale, bubble_scale = settings.BubbleScale, bubble_seconds = settings.BubbleSeconds,
        hide_on_fullscreen = settings.HideOnFullscreen, hidden_for_fullscreen = hiddenForFullscreen, follow_mouse = settings.FollowMouse,
        idle_seconds = settings.IdleSeconds, position = new { Left, Top }, saying = speech.IsSaying, reaction = behavior.Showing, trouble,
        clothes = Outfit.Clothes.Select(item => item.Label).ToArray(), accessories = Outfit.Accessories.Select(item => item.Label).ToArray(),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
