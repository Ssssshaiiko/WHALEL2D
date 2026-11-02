using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// FishPet.Mcp --check: starts a pet of its own out of sight, talks MCP to it the way an agent would, and keeps
// what her mirror showed in check\ beside bin\, so whoever installed her can see that it all works.
internal static class Check
{
    internal static async Task<int> Run()
    {
        var bin = AppContext.BaseDirectory;
        var root = Directory.GetParent(bin.TrimEnd('\\', '/'))!.FullName;
        var output = Path.Combine(root, "check");
        Directory.CreateDirectory(output);
        var pipeName = "FishPet.Check." + Guid.NewGuid().ToString("N");
        var settings = Path.Combine(Path.GetTempPath(), pipeName + ".json");
        var apiFile = Path.Combine(Path.GetTempPath(), pipeName + ".api.json");
        using var pet = Process.Start(new ProcessStartInfo(Path.Combine(bin, "FishPet.exe")) { ArgumentList = { "--test", "--pipe", pipeName, "--settings", settings, "--api", apiFile } })!;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Console.WriteLine("等她出来（第一次要编译着色器，可能要一会儿）……");
            await pipe.ConnectAsync(90000);
            var reader = new StreamReader(pipe, new UTF8Encoding(false));
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            int id = 0;
            async Task<JsonElement> Ask(string method, object? parameters = null)
            {
                int asked = ++id;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = asked, method, @params = parameters ?? new { } }));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                while (true)
                {
                    var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("桌宠断开了");
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var got) && got.ValueKind == JsonValueKind.Number && got.GetInt32() == asked) return doc.RootElement.Clone();
                }
            }
            async Task<JsonElement> Call(string tool, object arguments)
            {
                var result = (await Ask("tools/call", new { name = tool, arguments })).GetProperty("result");
                if (result.TryGetProperty("isError", out var error) && error.GetBoolean())
                    throw new InvalidOperationException(tool + "：" + result.GetProperty("content")[0].GetProperty("text").GetString());
                return result;
            }
            void Keep(JsonElement result, string file)
            {
                var image = result.GetProperty("content").EnumerateArray().First(block => block.GetProperty("type").GetString() == "image");
                File.WriteAllBytes(Path.Combine(output, file), Convert.FromBase64String(image.GetProperty("data").GetString()!));
                Console.WriteLine("  存好了：" + Path.Combine(output, file));
            }

            await Ask("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "check", version = "1" } });
            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            int tools = (await Ask("tools/list")).GetProperty("result").GetProperty("tools").GetArrayLength();
            Console.WriteLine($"✓ 连上了，有 {tools} 个工具");
            // She must be on screen (her first frames) before there is anything to see.
            for (int i = 0; i < 60; i++)
            {
                var state = JsonDocument.Parse((await Call("get_state", new { })).GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;
                if (state.GetProperty("trouble").ValueKind == JsonValueKind.String) throw new InvalidOperationException(state.GetProperty("trouble").GetString());
                try { await Call("look_in_mirror", new { }); break; }
                catch (InvalidOperationException) when (i < 59) { await Task.Delay(1000); }
            }
            Console.WriteLine("✓ 她出来了");
            await Call("say", new { text = "〔看过来 点头〕你好呀〔眯眼笑 脸红〕我准备好了！" });
            await Task.Delay(6000);
            Keep(await Call("look_back", new { }), "说话时的样子.png");
            Keep(await Call("look_in_mirror", new { @try = "歪头:右 笑眼 挑一下眉" }), "镜子里试的样子.png");
            Console.WriteLine("✓ 说话、自动拍照、照镜子都没问题");
            await CheckHttp(apiFile);
            await Call("quit", new { });
            pet.WaitForExit(10000);
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("✗ 检查没通过：" + error.Message);
            return 1;
        }
        finally
        {
            if (!pet.HasExited) pet.Kill();
            File.Delete(settings);
            File.Delete(apiFile);
        }
    }

    // The HTTP interface, for programs that talk to a model API themselves (HttpApi in FishPet).
    private static async Task CheckHttp(string apiFile)
    {
        var api = JsonDocument.Parse(File.ReadAllText(apiFile)).RootElement;
        string url = api.GetProperty("url").GetString()!, token = api.GetProperty("token").GetString()!;
        using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) };
        if ((await http.GetAsync("/tools")).StatusCode != System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("网页接口没口令也让进了");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        int tools = JsonDocument.Parse(await http.GetStringAsync("/tools?format=openai")).RootElement.GetArrayLength();
        var said = await http.PostAsync("/say", new StringContent("〔微笑 点头〕网页接口也通了", Encoding.UTF8, "text/plain"));
        if (!said.IsSuccessStatusCode) throw new InvalidOperationException("网页接口说话失败：" + await said.Content.ReadAsStringAsync());
        var state = await http.PostAsync("/call/get_state", new StringContent("{}", Encoding.UTF8, "application/json"));
        if (!state.IsSuccessStatusCode) throw new InvalidOperationException("网页接口调用工具失败：" + await state.Content.ReadAsStringAsync());
        Console.WriteLine($"✓ 网页接口也通了（" + url + $"，{tools} 个工具）");
    }
}
