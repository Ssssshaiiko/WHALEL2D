using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FishPet;

// The same tools for a program that talks to a model API itself and has no MCP: plain HTTP on this
// computer only (127.0.0.1), with a token so that no web page in a browser can drive her. Where it listens
// and the token are in api.json beside the plugin.
//   POST /say                 the body is what she says (text with 〔〕), or JSON { "text", "audio" }
//   POST /call/<tool>         JSON arguments; answers what the MCP tool answers (content: text and images)
//   GET  /tools[?format=…]    the tools: as MCP lists them, or format=openai / anthropic, ready for function calling
//   GET  /                    how she is (get_state)
// Every request carries the token: "Authorization: Bearer <token>".
internal sealed class HttpApi : IDisposable
{
    internal const int DefaultPort = 39555;
    private const int MostBody = 1024 * 1024;
    private readonly TcpListener listener;
    private readonly string token;
    private readonly Func<string, JsonElement, CancellationToken, Task<object>> execute;
    private readonly CancellationTokenSource lifetime = new();
    internal int Port { get; }

    private HttpApi(TcpListener listener, string token, Func<string, JsonElement, CancellationToken, Task<object>> execute)
    {
        this.listener = listener; this.token = token; this.execute = execute;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(Accept);
    }

    // The port kept in api.json if it is free, else the next free one; port 0 (a test) takes any.
    internal static HttpApi Start(string file, int port, Func<string, JsonElement, CancellationToken, Task<object>> execute)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        try
        {
            if (File.Exists(file) && JsonDocument.Parse(File.ReadAllText(file)).RootElement is var kept)
            {
                if (kept.TryGetProperty("token", out var keptToken) && keptToken.GetString() is { Length: >= 16 } known) token = known;
                if (port != 0 && kept.TryGetProperty("port", out var keptPort) && keptPort.TryGetInt32(out var used)) port = used;
            }
        }
        catch (Exception error) when (error is IOException or JsonException) { }
        for (int attempt = 0; ; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, port == 0 ? 0 : port + attempt);
            try
            {
                listener.Start();
                var api = new HttpApi(listener, token, execute);
                File.WriteAllText(file, JsonSerializer.Serialize(new { url = "http://127.0.0.1:" + api.Port, port = api.Port, token }, new JsonSerializerOptions { WriteIndented = true }));
                return api;
            }
            catch (SocketException) when (port != 0 && attempt < 20) { listener.Stop(); }
        }
    }

    private async Task Accept()
    {
        while (!lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(lifetime.Token); }
            catch (OperationCanceledException) { break; }
            catch (Exception error) when (error is SocketException or ObjectDisposedException) { if (lifetime.IsCancellationRequested) break; continue; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var connection = client;
        var stream = client.GetStream();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            var (method, target, headers, body) = await Read(stream, timeout.Token);
            if (headers.ContainsKey("origin")) { await Write(stream, 403, new { error = "不接受网页里发来的请求。" }); return; }
            if (!headers.TryGetValue("authorization", out var given) || given != "Bearer " + token)
            { await Write(stream, 401, new { error = "要带上 api.json 里的口令：Authorization: Bearer <token>" }); return; }
            var path = target.Split('?')[0].TrimEnd('/');
            var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";
            if (method == "GET" && path == "") { await Write(stream, 200, await execute("get_state", Empty(), timeout.Token)); return; }
            if (method == "GET" && path == "/tools") { await Write(stream, 200, ToolsAs(query.Contains("format=openai") ? "openai" : query.Contains("format=anthropic") ? "anthropic" : "mcp")); return; }
            if (method == "POST" && path == "/say")
            {
                var text = Encoding.UTF8.GetString(body);
                var arguments = text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : JsonSerializer.SerializeToElement(new { text });
                await Write(stream, 200, await execute("say", arguments, timeout.Token));
                return;
            }
            if (method == "POST" && path.StartsWith("/call/"))
            {
                var name = Uri.UnescapeDataString(path["/call/".Length..]);
                if (!ToolNames().Contains(name)) { await Write(stream, 404, new { error = "没有这个工具：" + name }); return; }
                var arguments = body.Length == 0 ? Empty() : JsonDocument.Parse(body).RootElement.Clone();
                await Write(stream, 200, await execute(name, arguments, timeout.Token));
                return;
            }
            await Write(stream, 404, new { error = "没有这个地址。能用的：POST /say、POST /call/<工具名>、GET /tools、GET /" });
        }
        catch (JsonException) { await Write(stream, 400, new { error = "JSON 写得不对。" }); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { await Write(stream, 400, Tools.Error(error.Message)); }
        catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception error) { Places.Log("网页接口出错：" + error); try { await Write(stream, 500, new { error = error.Message }); } catch { } }
    }

    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new { });
    private static string[] ToolNames() => Tools.Definitions.Select(tool => JsonSerializer.SerializeToElement(tool).GetProperty("name").GetString()!).ToArray();

    private static object ToolsAs(string format) => format switch
    {
        "openai" => Tools.Definitions.Select(tool => JsonSerializer.SerializeToElement(tool)).Select(tool => (object)new { type = "function", function = new {
            name = tool.GetProperty("name").GetString(), description = tool.GetProperty("description").GetString(), parameters = tool.GetProperty("inputSchema") } }).ToArray(),
        "anthropic" => Tools.Definitions.Select(tool => JsonSerializer.SerializeToElement(tool)).Select(tool => (object)new {
            name = tool.GetProperty("name").GetString(), description = tool.GetProperty("description").GetString(), input_schema = tool.GetProperty("inputSchema") }).ToArray(),
        _ => Tools.Definitions,
    };

    private static async Task<(string Method, string Target, System.Collections.Generic.Dictionary<string, string> Headers, byte[] Body)> Read(Stream stream, CancellationToken token)
    {
        var head = new MemoryStream();
        var one = new byte[1];
        // Up to the blank line that ends the headers.
        while (!EndsWithBlankLine(head))
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("连接断了");
            head.WriteByte(one[0]);
            if (head.Length > 16 * 1024) throw new IOException("请求头太长");
        }
        var lines = Encoding.UTF8.GetString(head.ToArray()).Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length < 2) throw new IOException("不是 HTTP 请求");
        var headers = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var line in lines.Skip(1).Where(line => line.Contains(':')))
            headers[line[..line.IndexOf(':')].Trim().ToLowerInvariant()] = line[(line.IndexOf(':') + 1)..].Trim();
        int length = headers.TryGetValue("content-length", out var value) && int.TryParse(value, out var parsed) ? parsed : 0;
        if (length < 0 || length > MostBody) throw new IOException("请求太大");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        return (start[0].ToUpperInvariant(), start[1], headers, body);
    }

    private static bool EndsWithBlankLine(MemoryStream head)
    {
        if (head.Length < 4) return false;
        var bytes = head.GetBuffer();
        long end = head.Length;
        return bytes[end - 4] == '\r' && bytes[end - 3] == '\n' && bytes[end - 2] == '\r' && bytes[end - 1] == '\n';
    }

    private static async Task Write(Stream stream, int status, object value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
    }

    public void Dispose()
    {
        lifetime.Cancel();
        listener.Stop();
    }
}
