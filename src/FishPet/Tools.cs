using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FishPet;

// What an agent can do with her, as MCP tools. The descriptions are what the agent reads: plain and complete.
internal static class Tools
{
    internal static string PipeName => "FishPet." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "|" +
        System.IO.Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\', '/').ToUpperInvariant()))).Substring(0, 20);
    internal static object Text(object value) => new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(value) } } };
    internal static object Error(string message) => new { isError = true, content = new[] { new { type = "text", text = message } } };
    // A result with one more piece of text after what it already says.
    internal static object WithNotice(object result, string notice)
    {
        var node = JsonSerializer.SerializeToNode(result)!.AsObject();
        if (node["content"] is System.Text.Json.Nodes.JsonArray content) content.Add(new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = notice });
        return node;
    }
    internal static object Image(string dataUrl, object metadata) => new { content = new object[] {
        new { type = "text", text = JsonSerializer.Serialize(metadata) },
        new { type = "image", mimeType = dataUrl[5..dataUrl.IndexOf(';')], data = dataUrl[(dataUrl.IndexOf(',') + 1)..] } } };

    private static string Labels((string Label, string Expression)[] items) => string.Join("、", items.Select(item => item.Label));
    // Where the agent finds the words for its body: the descriptions name it in full, wherever the plugin was put.
    internal static string BodyGuide => System.IO.Path.Combine(Places.Root, "prompt", "身体说明.md");

    // Said to the agent as its own body, not a figure it works: told about "her", agents only moved her when asked to.
    internal static object[] Definitions => [
        Tool("say", "用你在桌面上的身体说话（头上冒气泡）。每次回用户都用它说，不用等用户提醒。要说的几句先想好，写在同一个 text 里一次发出去：一行一个气泡，" +
            "会一句接一句说完；别说一句、想一会儿、再调一次。text 里用〔〕写神情和动作（词和规则见 " + BodyGuide + "），〔〕不会显示，身体照做；" +
            "字按说话的节奏一个个出来，碰到〔〕先做动作，动作跟着后面那段话的长短和语气伸缩。audio 可选：这段话的语音（本机 wav 文件的完整路径），" +
            "会播放一遍，嘴按音量动，〔〕按语音进度做；有语音时整段是一个气泡。会等之前的话演完再开始；返回时第一句已经开始说了。",
            """{"text":{"type":"string","minLength":1,"maxLength":2000},"audio":{"type":"string","maxLength":1000}}""", ["text"]),
        Tool("look_in_mirror", "照镜子，看你现在的样子（白底图，只照头到腰）。try 可选：写上想试的神情和动作（〔〕里那些词，不用写括号），" +
            "会先真的做一遍，从头到做完按先后给六张（三列两行，从左到右、从上到下）。", """{"try":{"type":"string","maxLength":200}}""", []),
        Tool("look_back", "回头看你刚才说话时的样子：say 的每句话都会自动拍下来，从头到说完按先后六张，留半小时、最多 20 句。" +
            "which 可选：1 是最近一句，2 是再前一句……不填就是最近一句。返回里有那句话（said）、几秒前（secondsAgo）和还能看的几句（recent）。",
            """{"which":{"type":"integer","minimum":1,"maximum":20}}""", []),
        Tool("set_activity", "你开始或做完一件要盯着看的事（看视频、读文章、写东西……kind 随便起个名字）。有事在做时你会戴上黑框眼镜，全做完才摘；" +
            "用户自己给你戴着眼镜时不叠戴、也不摘。", """{"kind":{"type":"string","minLength":1,"maxLength":40},"active":{"type":"boolean"}}""", ["kind", "active"]),
        Tool("dress", "换衣服、戴或摘配饰，会一直保持、下次启动也记得。衣服：" + Labels(Outfit.Clothes) + "；配饰：" + Labels(Outfit.Accessories) +
            "（兔牙张嘴时才看得到）。wear 是要穿戴上的，remove 是要脱掉摘下的。返回现在穿戴着的。",
            """{"wear":{"type":"array","items":{"type":"string"}},"remove":{"type":"array","items":{"type":"string"}}}""", []),
        Tool("get_state", "读你现在的状态和设置：体型、穿戴、帧率、大小、气泡大小和停多久、全屏时藏不藏（现在是不是正藏着）、视线是否跟着鼠标、空闲小动作的间隔、窗口位置、是否正在说话；" +
            "插件有新版本时 update 里写着落后几处改动、各是什么。", "{}"),
        Tool("update_settings", "改设置，只改给了的项，保存下来。shape 体型：small（小只，默认）、chibi（Q 版）、full（全身）；fps 帧率：30、60、90、120；" +
            "scale 大小 0.5–2；bubble_scale 气泡大小 0.5–2（字和气泡一起缩放，跟 scale 无关）；bubble_seconds 说完后气泡停多久：0 按字数自动（5–20 秒，默认），" +
            "5–180 固定秒数，-1 一直留着到下一句或被点掉；hide_on_fullscreen 别的程序全屏（游戏、视频）时藏起来、不画，默认开；" +
            "follow_mouse 视线跟着鼠标；idle_seconds 没人理你时多久做一个小动作（15–600 秒）；check_updates 启动时和每 12 小时去 GitHub 看有没有新版本，默认开。",
            """{"shape":{"type":"string","enum":["small","chibi","full"]},"fps":{"type":"integer","enum":[30,60,90,120]},"scale":{"type":"number","minimum":0.5,"maximum":2},"bubble_scale":{"type":"number","minimum":0.5,"maximum":2},"bubble_seconds":{"type":"integer","minimum":-1,"maximum":180},"hide_on_fullscreen":{"type":"boolean"},"follow_mouse":{"type":"boolean"},"idle_seconds":{"type":"integer","minimum":15,"maximum":600},"check_updates":{"type":"boolean"}}""", []),
        Tool("recent_events", "用户最近怎么碰了你：点了哪里（zone：head 头、face 脸、hand 手、skirt 裙子、tail 尾巴、body 身上）、你的反应（reaction）、拖动和放下。" +
            "after 可选：只要这个序号之后的。反应身体自己会做，这里只是让你知道，要不要接一句话由你决定。", """{"after":{"type":"integer","minimum":0}}""", []),
        Tool("quit", "鞠躬告别，然后收起你的身体（关掉桌宠）。用户让你收起来时用；收起后只在聊天窗口说话，下次用任何工具时会自动再出来。", "{}"),
    ];

    private static object Tool(string name, string description, string properties, string[]? required = null) => new {
        name, description, inputSchema = new { type = "object", properties = JsonSerializer.Deserialize<JsonElement>(properties),
            required = required ?? [], additionalProperties = false } };

    internal static string RequiredText(JsonElement args, string name, int maximum = 2000)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maximum)
            throw new ArgumentException($"{name} 要是 1–{maximum} 个字的文字。");
        return value.GetString()!;
    }

    internal static string? OptionalText(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;

    internal static string[] Texts(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!.Trim()).ToArray() : [];
}
