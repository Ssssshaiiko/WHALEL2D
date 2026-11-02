using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

// The MCP server an agent starts (stdio). It only relays to the pet, which speaks MCP over a pipe of this
// user's own; if she is not up yet it starts her first, outside the agent's process tree, so closing the
// agent does not close her.
//   --pipe <name>  another pet's pipe
//   --check        install check (Check.cs)
Console.OutputEncoding = new UTF8Encoding(false);
if (args.Contains("--check")) return await Check.Run();
try
{
    int at = Array.IndexOf(args, "--pipe");
    var pipeName = at >= 0 && at + 1 < args.Length ? args[at + 1] : "FishPet." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))).Substring(0, 20);
    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    try { await pipe.ConnectAsync(1500); }
    catch (TimeoutException) when (at < 0)
    {
        var pet = Path.Combine(AppContext.BaseDirectory, "FishPet.exe");
        if (!File.Exists(pet)) throw new FileNotFoundException("没找到 FishPet.exe，先运行插件里的 setup.ps1。");
        // Started by Explorer, she is not a child of the agent.
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + pet + "\"") { UseShellExecute = false })?.Dispose(); }
        catch { Process.Start(new ProcessStartInfo(pet) { UseShellExecute = true })?.Dispose(); }
        // The first start compiles her shaders and may take a while.
        await pipe.ConnectAsync(60000);
    }
    using var cancel = new CancellationTokenSource();
    var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
    var toPet = input.CopyToAsync(pipe, cancel.Token); var toAgent = pipe.CopyToAsync(output, cancel.Token);
    await Task.WhenAny(toPet, toAgent);
    cancel.Cancel(); pipe.Dispose();
    try { await toAgent; } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
}
catch (Exception e)
{
    await Console.Error.WriteLineAsync("大肥鱼桌宠连不上：" + e.Message);
    return 1;
}
return 0;
