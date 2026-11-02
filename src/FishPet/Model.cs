using System;
using System.IO;
using System.Linq;

namespace FishPet;

// The model's three body types. Fractions are of the frame: HeadTop is the top of her hair, HeadAt her
// eyes, FootAt her soles, Side the margin left and right of her body. FeetY is where her soles are on the
// model and Reach how far she travels when she jumps. The frame leaves her room to jump.
internal sealed record Shape(string Id, string Label, string? Body, float Zoom, float CenterY,
    int Width, int Height, double HeadTop, double HeadAt, double FootAt, double Side, float FeetY, float Reach)
{
    internal static readonly Shape[] All =
    {
        new("small", "小只", "体型1", 1.626f, .319f, 340, 620, .106, .241, .991, .15, -.2845f, 1),
        new("chibi", "Q 版", "体型2", 3.4f, .53f, 300, 300, .09, .42, .87, .22, .3124f, .45f),
        new("full", "全身", null, 1, 0, 340, 680, .11, .2, .96, .15, -.92f, 1.7f),
    };
    internal static Shape Find(string? id) => All.FirstOrDefault(shape => shape.Id == id) ?? All[0];
}

// What she wears stays on until it is changed, unlike her faces: label, then the model's expression.
internal static class Outfit
{
    internal static readonly (string Label, string Expression)[] Clothes =
    {
        ("长裙", "裙子切换"), ("胸口衣服", "胸口衣服"), ("脱鞋", "脱鞋"), ("收起尾巴", "尾巴开关"),
    };
    internal static readonly (string Label, string Expression)[] Accessories =
    {
        ("黑框眼镜", "黑框眼镜"), ("单片眼镜", "单片眼镜"), ("钢盆", "钢盆"), ("兔牙", "兔牙"),
    };
    internal static string? Expression(string label) =>
        Clothes.Concat(Accessories).FirstOrDefault(item => item.Label == label || item.Expression == label).Expression;
}

// Where things are. The plugin folder holds bin\ (this program), renderer\build\Release (the renderer each
// user builds against their own Cubism SDK) and model\ (the model each user downloads from its author).
internal static class Places
{
    internal const string ModelFile = "大肥鱼.model3.json";
    internal static string Root { get; } = FindRoot();
    internal static string Renderer => Path.Combine(Root, "renderer", "build", "Release", "l2d-render.exe");
    internal static string Cache => Path.Combine(Root, "cache");
    // A test keeps its settings apart (--settings).
    internal static string SettingsFile { get; set; } = Path.Combine(Root, "settings.json");
    internal static string ErrorLog => Path.Combine(Root, "errors.log");
    // Where the HTTP interface listens and its token (HttpApi); a test keeps its own (--api).
    internal static string ApiFile { get; set; } = Path.Combine(Root, "api.json");

    // The model wherever in model\ it was unpacked, or where --model says.
    internal static string? Model(string? given)
    {
        if (given != null) return File.Exists(given) ? Path.GetFullPath(given) : null;
        var folder = Path.Combine(Root, "model");
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, ModelFile, SearchOption.AllDirectories).FirstOrDefault() : null;
    }

    private static string FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            if (Directory.Exists(Path.Combine(folder.FullName, "renderer")) && Directory.Exists(Path.Combine(folder.FullName, "model"))) return folder.FullName;
        return AppContext.BaseDirectory;
    }

    internal static void Log(string message)
    {
        try { File.AppendAllText(ErrorLog, DateTime.Now.ToString("s") + " " + message + Environment.NewLine); } catch { }
    }
}
