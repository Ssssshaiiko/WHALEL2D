using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace FishPet;

// Starts her once per user: a second start (the MCP bridge starting her while she is already up) just ends.
//   --model <大肥鱼.model3.json>  the model somewhere else than model\
//   --test --pipe <name> --settings <file> --api <file>  out of sight, on a pipe, settings and HTTP port of its own (FishPet.Mcp --check)
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? Option(string name) { int at = Array.IndexOf(args, name); return at >= 0 && at + 1 < args.Length ? args[at + 1] : null; }
        bool test = args.Contains("--test");
        if (Option("--settings") is { } file) Places.SettingsFile = file;
        if (Option("--api") is { } apiFile) Places.ApiFile = apiFile;
        using var single = new Mutex(true, "FishPet.Single." + Tools.PipeName, out bool first);
        if (!first && !test) return 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Places.Log("出错了：" + e.Exception); e.Handled = true; };
        new PetWindow(Settings.Load(), Places.Model(Option("--model")), test, Option("--pipe")).Show();
        return app.Run();
    }
}
