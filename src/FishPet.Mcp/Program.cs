using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// The MCP server an agent starts (stdio). It relays to the pet, which speaks MCP over a pipe of this user's own; if
// she is not up yet it starts her first, outside the agent's process tree, so the agent closing does not cut her off
// mid-word: with no agent left she bows out by herself (PetWindow.GoWithAgent).
// When she goes (quit, or she fell over) it stays: an agent that restarts a server that ended (DeepSeek Harness does,
// half a second later) had her straight back three seconds after quit, and one that does not lost her tools. She
// comes back for the next thing that needs her (the owner, 2026-11-03).
//   --pipe <name>  another pet's pipe (never started from here)
//   --check        install check (Check.cs)
Console.OutputEncoding = new UTF8Encoding(false);
if (args.Contains("--check")) return await Check.Run();
int at = Array.IndexOf(args, "--pipe");
var relay = new Relay(at >= 0 && at + 1 < args.Length ? args[at + 1] : null);
try { await relay.Connect(); }
catch (Exception e)
{
    await Console.Error.WriteLineAsync("大肥鱼桌宠连不上：" + e.Message);
    return 1;
}
await relay.Run();
return 0;

// The agent's messages to her and hers back, a line each. While she is away it answers a ping and the tool list
// itself (as she last gave it), and anything else starts her again and tells her the agent's handshake once more.
internal sealed class Relay(string? givenPipe)
{
    private const string Ours = "fishpet-relay-";
    private readonly string pipeName = givenPipe ?? "FishPet." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "|" +
        Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\', '/').ToUpperInvariant()))).Substring(0, 20);
    private readonly Stream output = Console.OpenStandardOutput();
    private readonly SemaphoreSlim writing = new(1, 1);
    // What she was asked and has not answered (id as JSON → method): answered for her if she goes.
    private readonly ConcurrentDictionary<string, string> waiting = new();
    // Set while she is there; her reader clears it when she goes.
    private volatile NamedPipeClientStream? pipe;
    private volatile StreamWriter? toPet;
    private string? hello, ready;
    private JsonNode? tools;
    private int greetings;

    internal async Task Connect()
    {
        var next = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            try { await next.ConnectAsync(1500); }
            catch (TimeoutException) when (givenPipe == null)
            {
                var pet = Path.Combine(AppContext.BaseDirectory, "FishPet.exe");
                if (!File.Exists(pet)) throw new FileNotFoundException("没找到 FishPet.exe，先运行插件里的 setup.ps1。");
                // Started by Explorer, she is not a child of the agent.
                try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + pet + "\"") { UseShellExecute = false })?.Dispose(); }
                catch { Process.Start(new ProcessStartInfo(pet) { UseShellExecute = true })?.Dispose(); }
                // The first start compiles her shaders and may take a while.
                await next.ConnectAsync(60000);
            }
            Pipe.EnsureMine(next);
        }
        catch { next.Dispose(); throw; }
        toPet = new StreamWriter(next, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
        pipe = next;
        _ = Task.Run(() => FromPet(next));
    }

    internal async Task Run()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            JsonObject? message = null;
            try { message = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { }
            var method = message?["method"]?.GetValueKind() == JsonValueKind.String ? message["method"]!.GetValue<string>() : null;
            var id = message != null && message.TryGetPropertyValue("id", out var given) && given != null ? given.ToJsonString() : null;
            if (method == "initialize") hello = line;
            else if (method == "notifications/initialized") ready = line;
            if (pipe == null)
            {
                if (id == null) continue;   // A notification for her while she is away.
                if (method == "ping") { await ToAgent(Answer(id, new JsonObject())); continue; }
                if (method == "tools/list" && tools != null) { await ToAgent(Answer(id, tools.DeepClone())); continue; }
                try { await Return(retell: method != "initialize"); }
                catch (Exception error) { await ToAgent(Failure(id, "大肥鱼桌宠连不上：" + error.Message)); continue; }
            }
            if (id != null && method != null) waiting[id] = method;
            try { await toPet!.WriteLineAsync(line); }
            catch (Exception error) when (error is IOException or ObjectDisposedException or NullReferenceException)
            {
                if (id != null && waiting.TryRemove(id, out _)) await ToAgent(Failure(id, "桌宠刚好关掉了，再用一次工具她会重新出来。"));
            }
        }
        pipe?.Dispose();
    }

    // Back for the next thing that needs her: started again, and told the agent's handshake as it was, under an id of
    // our own so her answer to it goes nowhere. She reads her messages in order, so what follows waits for nothing.
    private async Task Return(bool retell)
    {
        await Connect();
        if (!retell || hello == null) return;
        var again = JsonNode.Parse(hello)!;
        again["id"] = Ours + ++greetings;
        await toPet!.WriteLineAsync(again.ToJsonString());
        if (ready != null) await toPet.WriteLineAsync(ready);
    }

    private async Task FromPet(NamedPipeClientStream from)
    {
        using var reader = new StreamReader(from, new UTF8Encoding(false), false, 4096, true);
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                if (Answered(line) is not { } id || !id.StartsWith("\"" + Ours)) await ToAgent(line);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        // She is gone. What she was asked and did not answer is answered for her; she comes back for the next thing.
        if (pipe == from) { pipe = null; toPet = null; }
        from.Dispose();
        foreach (var id in waiting.Keys)
            if (waiting.TryRemove(id, out _)) await ToAgent(Failure(id, "桌宠关掉了，再用一次工具她会重新出来。"));
    }

    // Which request a line of hers answers (its id as JSON), keeping her tool list for while she is away.
    private string? Answered(string line)
    {
        JsonObject? message = null;
        try { message = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { }
        if (message == null || message.ContainsKey("method") || !message.TryGetPropertyValue("id", out var given) || given == null) return null;
        var id = given.ToJsonString();
        if (waiting.TryRemove(id, out var method) && method == "tools/list" && message["result"] is { } result) tools = result.DeepClone();
        return id;
    }

    private async Task ToAgent(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await writing.WaitAsync();
        try { await output.WriteAsync(bytes); await output.FlushAsync(); }
        finally { writing.Release(); }
    }

    private static string Answer(string id, JsonNode result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id), ["result"] = result }.ToJsonString();
    private static string Failure(string id, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id), ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message } }.ToJsonString();
}

internal static class Pipe
{
    // The pet's pipe must be this user's, so no one else's program poses as her. Checked against the user and not,
    // as PipeOptions.CurrentUserOnly does, against the token's default owner: that is Administrators for some agent
    // hosts' processes (DeepSeek Harness, 2026-11-03), and the pet, started by Explorer, is not one of them.
    internal static void EnsureMine(NamedPipeClientStream pipe)
    {
        if (pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != WindowsIdentity.GetCurrent().User)
            throw new UnauthorizedAccessException("这个桌宠不是当前用户开的。");
    }
}
