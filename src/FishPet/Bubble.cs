using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace FishPet;

// One continuous outline joins the rounded body and its small pointer.
internal sealed class Bubble : Border
{
    internal static readonly Color HairBlue = Color.FromRgb(80, 96, 152);
    internal const double PointerHeight = 9;

    internal Bubble()
    {
        Background = new SolidColorBrush(Color.FromRgb(255, 251, 244));
        BorderBrush = new SolidColorBrush(HairBlue);
        BorderThickness = new Thickness(1.5);
        CornerRadius = new CornerRadius(24);
        Padding = new Thickness(18, 14, 14, 14 + PointerHeight);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double inset = BorderThickness.Left / 2;
        double width = ActualWidth - inset * 2;
        double height = ActualHeight - PointerHeight - inset * 2;
        if (width <= 0 || height <= 0) return;
        double radius = Math.Min(CornerRadius.TopLeft, Math.Min(width, height) / 2);
        var body = new RectangleGeometry(new Rect(inset, inset, width, height), radius, radius);
        double x = ActualWidth / 2, seam = inset + height - 1, tip = ActualHeight - inset;
        var pointer = new StreamGeometry();
        using (var context = pointer.Open())
        {
            context.BeginFigure(new Point(x - 8, seam), true, true);
            context.LineTo(new Point(x, tip), true, false);
            context.LineTo(new Point(x + 8, seam), true, false);
        }
        drawing.DrawGeometry(Background, new Pen(BorderBrush, BorderThickness.Left) { LineJoin = PenLineJoin.Round },
            new CombinedGeometry(GeometryCombineMode.Union, body, pointer));
    }
}

// Her speech bubble: its own small window over her head, kept inside the screen, never taking the focus.
internal sealed class BubbleWindow : Window
{
    private readonly Window pet;
    private readonly Func<double> headTop;
    internal TextBlock Text { get; } = new()
    {
        TextWrapping = TextWrapping.Wrap, FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(50, 56, 84)),
        FontFamily = new FontFamily("Microsoft YaHei UI"), LineHeight = 22,
    };

    internal BubbleWindow(Window pet, Func<double> headTop)
    {
        this.pet = pet; this.headTop = headTop;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true; SizeToContent = SizeToContent.WidthAndHeight;
        Content = new Bubble { MaxWidth = 300, Child = Text };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Never activated, not in Alt+Tab.
            SetWindowLongPtr(handle, -20, (nint)((long)GetWindowLongPtr(handle, -20) | 0x8000080));
        };
        SizeChanged += (_, _) => Place();
        pet.LocationChanged += (_, _) => Place();
        pet.SizeChanged += (_, _) => Place();
        // A click on the bubble closes it.
        MouseLeftButtonUp += (_, _) => Hide();
    }

    // In a test the bubble only keeps its words, out of the user's sight.
    internal bool Quiet { get; set; }

    // How big the bubble is (settings: bubble_scale): the words, the outline and how wide it may grow, together.
    internal double Scale
    {
        set { ((FrameworkElement)Content).LayoutTransform = new ScaleTransform(value, value); Place(); }
    }

    internal void Say(string text)
    {
        Text.Text = text;
        if (!IsVisible && !Quiet) Show();
        Place();
    }

    internal void Place()
    {
        if (!IsVisible) return;
        var area = SystemParameters.WorkArea;
        double left = pet.Left + pet.Width / 2 - ActualWidth / 2, top = pet.Top + headTop() - ActualHeight + 6;
        Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}

// What she says, word by word at a speaking pace (Line): the bubble only changes with her first word,
// after whatever she does before speaking, and her mouth moves while words come out.
internal sealed class Speech
{
    private readonly BubbleWindow bubble;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly System.Diagnostics.Stopwatch elapsed = new();
    private Line? line;
    private int shown;
    private DateTime hideAt = DateTime.MaxValue;
    // How long the bubble stays after the last word (settings: bubble_seconds): by default a little longer for more
    // words; -1 keeps it until her next line or a click.
    internal static TimeSpan StaysFor(string words) => TimeSpan.FromSeconds(Math.Clamp(4 + words.Length * .15, 5, 20));
    internal int Seconds { get; set; }
    private DateTime Until(string words) => Seconds switch
    {
        -1 => DateTime.MaxValue,
        0 => DateTime.UtcNow + StaysFor(words),
        var seconds => DateTime.UtcNow.AddSeconds(seconds),
    };

    internal Speech(BubbleWindow bubble)
    {
        this.bubble = bubble;
        timer.Tick += (_, _) => Tick();
    }

    internal bool IsSaying => timer.IsEnabled;
    internal bool IsTalking => timer.IsEnabled && line?.Talking == true;
    internal string Words => line?.Plain ?? "";
    internal double Spoken => line?.Spoken ?? 0;

    internal void Say(Line said)
    {
        line = said; shown = 0; hideAt = DateTime.MaxValue;
        elapsed.Restart();
        timer.Start();
        Tick();
    }

    // Called by the pet's own clock too, so a bubble left alone goes away.
    internal void Tick()
    {
        if (timer.IsEnabled && line is { } current)
        {
            current.Advance(elapsed.Elapsed.TotalSeconds);
            if (current.Shown != shown && current.Shown > 0)
            {
                shown = current.Shown;
                bubble.Say(current.Plain[..shown]);
            }
            if (current.Done)
            {
                timer.Stop();
                if (current.Plain.Length > 0) { bubble.Say(current.Plain); hideAt = Until(current.Plain); }
            }
        }
        if (!timer.IsEnabled && DateTime.UtcNow >= hideAt) { bubble.Hide(); hideAt = DateTime.MaxValue; }
    }

    internal void Stop() { timer.Stop(); bubble.Hide(); hideAt = DateTime.MaxValue; }
}
