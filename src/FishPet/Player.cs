using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FishPet;

// The renderer process: frames arrive one at a time over its stdout (a 16-byte header "L2DF", width,
// height, 0, then premultiplied BGRA), commands go to its stdin. It dies with this program (Job).
internal sealed class Player : IDisposable
{
    private const int FrameMagic = 0x4644324C, ReadyFrames = 20;
    private readonly Image image;
    private readonly Job job = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Queue<string> errors = new();
    private WriteableBitmap? bitmap;
    private Process? renderer;
    private volatile bool disposed;
    private bool failed;
    internal long Frames { get; private set; }
    internal event Action? Ready;
    internal event Action<string>? Failed;
    internal Player(Image target) => image = target;

    internal void Start(string model, Shape shape, IEnumerable<string> outfit, int width, int height, int fps)
    {
        var start = new ProcessStartInfo(Places.Renderer)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "--model", model, "--width", width.ToString(), "--height", height.ToString(), "--texscale", "0.25",
            "--masksize", "1024", "--cache", Places.Cache, "--pipe", "--fps", fps.ToString() }) start.ArgumentList.Add(arg);
        var process = new Process { StartInfo = start };
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data == null) return;
            lock (errors) { errors.Enqueue(line.Data); if (errors.Count > 6) errors.Dequeue(); }
        };
        try
        {
            Directory.CreateDirectory(Places.Cache);
            process.Start();
            renderer = process;
            if (!job.Attach(process)) throw new IOException("无法关联渲染进程");
            process.StandardInput.AutoFlush = true;
            process.BeginErrorReadLine();
            foreach (var worn in outfit) Send($"exp {worn} 1");
            SetShape(shape);
            _ = Task.Run(() => ReadFrames(process));
            _ = WatchFirstFrame();
        }
        catch (Exception ex) { process.Dispose(); Fail(ex.Message); }
    }

    private async Task WatchFirstFrame()
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), cancellation.Token); } catch (OperationCanceledException) { return; }
        await image.Dispatcher.InvokeAsync(() => { if (Frames == 0) Fail("渲染程序一直没有出画面"); });
    }

    private async Task ReadFrames(Process process)
    {
        var token = cancellation.Token;
        var stream = process.StandardOutput.BaseStream;
        var header = new byte[16];
        var data = Array.Empty<byte>();
        try
        {
            while (true)
            {
                await stream.ReadExactlyAsync(header, token);
                if (BitConverter.ToInt32(header, 0) != FrameMagic) throw new IOException("渲染帧头不对");
                int width = BitConverter.ToInt32(header, 4), height = BitConverter.ToInt32(header, 8);
                if (data.Length != width * height * 4) data = new byte[width * height * 4];
                await stream.ReadExactlyAsync(data, token);
                // Never reuse the frame buffer before the dispatcher has copied it.
                await image.Dispatcher.InvokeAsync(() =>
                {
                    if (disposed) return;
                    if (bitmap == null || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
                    {
                        bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
                        image.Source = bitmap;
                    }
                    bitmap.WritePixels(new Int32Rect(0, 0, width, height), data, width * 4, 0);
                    if (++Frames == ReadyFrames) Ready?.Invoke();
                }, DispatcherPriority.Render, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            string detail;
            lock (errors) detail = string.Join(" ", errors);
            await image.Dispatcher.InvokeAsync(() => Fail(ex.Message + " " + detail));
        }
    }

    private void Fail(string message)
    {
        if (disposed || failed) return;
        failed = true;
        Failed?.Invoke(message);
    }

    internal void Send(string command)
    {
        try { if (renderer is { HasExited: false }) renderer.StandardInput.WriteLine(command); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    internal void SetShape(Shape shape)
    {
        foreach (var body in Shape.All.Select(s => s.Body).OfType<string>()) Send($"exp {body} {(shape.Body == body ? 1 : 0)}");
        Send(FormattableString.Invariant($"view {shape.Zoom} 0 {shape.CenterY}"));
        Send(FormattableString.Invariant($"anchor {shape.FeetY} {shape.Reach}"));
        Send("settle");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel();
        Send("quit");
        job.Dispose();
        renderer?.Dispose();
        cancellation.Dispose();
    }
}

// Clicks only count on her, not on the transparent window around her or her faintest edges.
internal sealed class SpriteImage : Image
{
    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters)
    {
        if (Source is not BitmapSource bitmap || ActualWidth <= 0 || ActualHeight <= 0) return null;
        int x = (int)(parameters.HitPoint.X * bitmap.PixelWidth / ActualWidth);
        int y = (int)(parameters.HitPoint.Y * bitmap.PixelHeight / ActualHeight);
        if (x < 0 || y < 0 || x >= bitmap.PixelWidth || y >= bitmap.PixelHeight) return null;
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3] >= 16 ? new PointHitTestResult(this, parameters.HitPoint) : null;
    }
}
