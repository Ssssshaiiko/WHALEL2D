using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FishPet;

// A face made of the model's switches and face parameters, held for a few seconds, and the body moving with it.
// Motion is what her body does meanwhile: a parameter or a View* track of her whole figure, then seconds:value
// keys that start and end at 0; EyeOpen closes her eyes as far as it goes below 0. Pushed marks a movement that
// turns with a side: away from where she was touched, or either way at random when she moves by herself.
internal sealed record Mood(string[] Expressions, (string Id, double Value)[] Parameters, double Seconds = 3,
    (string Id, string Keys)[]? Motion = null, (string On, string Off)? Gesture = null, bool Pushed = false);

// Her reflexes on the Live2D display: what she does by herself when clicked, dragged, left alone or busy.
// Built only for this display, nothing here reads the clip rules. It gives the renderer commands and
// knows nothing of windows, so all of it is checked in FishPet.Tests.
internal sealed class Behavior
{
    private const string Glasses = "黑框眼镜", Monocle = "单片眼镜";
    private static readonly TimeSpan ClickSpan = TimeSpan.FromSeconds(2.5);
    private const int ClicksForBasin = 5;

    internal static readonly IReadOnlyDictionary<string, Mood> Moods = new Dictionary<string, Mood>
    {
        ["问候"] = new([], [("EyeSmile", 1), ("ParamMouthForm", 1)], 4.4, [("FaceAngleZ", "0:0 .5:7 1:5 1.5:0 3.4:0 3.8:7 4.4:0"), ("LeanInput", "0:0 1.1:0 1.7:-11 2.6:-11 3.3:0"), ("FaceAngleY", "0:0 1.1:0 1.7:-19 2.2:-17 2.6:-19 3.3:0"), ("ViewSquash", "0:0 1.1:0 1.7:.04 2.6:.04 3.3:0"), ("EyeOpen", "0:0 1.2:0 1.6:-1 2.7:-1 3.1:0 4.4:0")], ("默认手切举手", "举手切默认手")),
        ["告别"] = new([], [("EyeSmile", 1), ("ParamMouthForm", 1)], 3, [("FaceAngleZ", "0:0 .4:6 .8:0"), ("LeanInput", "0:0 .7:0 1.3:-12 2.6:-12 3:0"), ("FaceAngleY", "0:0 .7:0 1.3:-19 2.6:-19 3:0"), ("ViewSquash", "0:0 .7:0 1.3:.04 2.6:.04 3:0"), ("EyeOpen", "0:0 .8:0 1.2:-1 2.6:-1 3:0")], ("默认手切举手", "举手切默认手")),
        ["落地"] = new(["圈圈眼"], [("BrowInnerUp", .6), ("ParamMouthOpenY", .2)], 1.25, [("ViewSquash", "0:0 .1:.06 .3:-.02 .5:.015 .75:0"), ("FaceAngleY", "0:0 .1:-10 .75:0"), ("FaceAngleX", "0:0 .5:0 .65:-8 .8:8 .95:-6 1.1:4 1.3:0"), ("EyeOpen", "0:0 1.15:0 1.25:-1 1.4:0")]),
        ["害羞"] = new(["脸红"], [("FaceAngleY", -12), ("FaceAngleX", 10), ("FaceAngleZ", 9), ("ParamEyeBallX", -.6), ("ParamEyeBallY", -.5), ("MouthPressLipOpen", .6)], 3, [("FaceAngleZ", "0:0 .5:2 1:-1 1.6:2 2.2:-1 3:0"), ("ViewSquash", "0:0 .4:.02 2.4:.02 3:0")]),
        ["生气"] = new(["黑脸"], [("CheeckPuff", 1), ("ParamBrowLY", -.8), ("ParamBrowRY", -.8), ("ParamBrowLAngle", -1), ("ParamBrowRAngle", -1), ("FaceAngleX", -14), ("FaceAngleY", -6)], 3, [("FaceAngleX", "0:0 .15:-8 .3:6 .45:-5 .6:3 .8:0"), ("ViewSquash", "0:0 .08:.04 .25:0 1:0"), ("LeanInput", "0:0 .2:8 1.6:6 2.2:0")]),
        ["得意"] = new([], [("REyeSquint", .8), ("LEyeSquint", .8), ("ParamMouthForm", 1), ("MouthX", .5), ("FaceAngleY", 10), ("FaceAngleZ", 12)], 3, [("FaceAngleX", "0:0 .5:3 1.2:-2 1.9:3 3:0"), ("LeanInput", "0:0 .35:6 2.4:5 3:0")]),
        ["委屈"] = new(["流泪"], [("BrowInnerUp", 1), ("Mouthpucker", 1), ("FaceAngleY", -10), ("FaceAngleZ", -6)], 3, [("ViewSquash", "0:0 .4:.03 2.4:.025 3:0"), ("FaceAngleX", "0:0 .4:-3 .9:2 1.4:-3 1.9:2 3:0")]),
        ["开心"] = new([], [("EyeSmile", 1), ("EyeOpen", 0), ("ParamMouthForm", 1), ("ParamMouthOpenY", .5), ("FaceAngleY", 6)], 3, [("FaceAngleZ", "0:0 .3:-10 .7:10 1.1:-8 1.5:6 2:0"), ("FaceAngleY", "0:0 .3:6 .7:2 1.1:6 1.5:2 2:0")], ("默认手切举手", "举手切默认手")),
        ["心虚"] = new(["流冷汗"], [("ParamEyeBallX", .8), ("ParamEyeBallY", .3), ("MouthX", .6), ("ParamMouthForm", -.5), ("FaceAngleX", 14), ("FaceAngleY", -8)], 3, [("FaceAngleZ", "0:0 .3:-4 .6:2 .9:-3 1.2:2 1.5:-2 2:0"), ("ViewSquash", "0:0 .3:.03 2:.02 2.6:0"), ("LeanInput", "0:0 .3:6 2:5 2.6:0")], Pushed: true),
        ["震惊"] = new(["圈圈眼"], [("ParamMouthOpenY", .8), ("JawOpen", .5), ("FaceAngleX", -16), ("FaceAngleY", -8), ("ParamEyeBallX", -.6), ("ParamEyeBallY", -.5)], 3, [("ViewSquash", "0:0 .06:.06 .18:-.05 .4:.05 .6:0"), ("ViewY", "0:0 .06:0 .2:.06 .4:0"), ("FaceAngleY", "0:0 .2:16 .5:4 1.4:0")], Pushed: true),
        ["思考"] = new([], [("ParamEyeBallY", .7), ("ParamEyeBallX", .4), ("FaceAngleZ", 12), ("FaceAngleY", 6), ("BrowInnerUp", .6)], 3.4, [("FaceAngleX", "0:0 .6:4 1.4:2 2.2:5 3:0")], ("竖手指切出来动画", "竖手指切回默认")),
        ["摸头"] = new(["脸红"], [("EyeSmile", .9), ("ParamMouthForm", 1)], 3, [("FaceAngleY", "0:0 .25:-18 .7:-12 1.1:-19 1.5:-12 1.9:-16 2.4:0"), ("FaceAngleZ", "0:0 .5:14 1.1:-14 1.7:14 2.4:0"), ("ViewRoll", "0:0 .5:2 1.1:-2 1.7:2 2.4:0"), ("ViewSquash", "0:0 .25:.05 1.9:.04 2.4:0")]),
        ["戳脸"] = new(["＞＜"], [("CheeckPuff", .8), ("FaceAngleX", 14), ("FaceAngleZ", 8)], 3, [("FaceAngleX", "0:0 .08:12 .3:6 1.2:0"), ("FaceAngleY", "0:0 .08:-6 .4:-2 1.2:0"), ("ViewSquash", "0:0 .06:.04 .25:0 1.2:0")], Pushed: true),
        ["挨打"] = new(["钢盆", "流冷汗"], [("ParamMouthForm", -.6), ("FaceAngleY", -12)], 4, [("ViewSquash", "0:0 .09:.11 .3:.02 .5:.05 .75:.01 1.4:0"), ("FaceAngleZ", "0:0 .15:10 .4:-8 .65:6 .9:-4 1.2:2 1.6:0"), ("FaceAngleY", "0:0 .1:-12 .5:-4 1.6:0")]),
        ["护裙"] = new(["黑脸", "脸红"], [("CheeckPuff", .8), ("ParamBrowLY", -.6), ("ParamBrowRY", -.6), ("FaceAngleX", -16), ("FaceAngleY", -8), ("ParamEyeBallX", .5)], 3, [("FaceAngleZ", "0:0 .2:-12 .5:-7 1.6:-8 2.8:0"), ("ViewSquash", "0:0 .12:.05 .35:.01 2.8:0"), ("LeanInput", "0:0 .3:5 2.2:4 2.8:0")], ("默认手切举手", "举手切默认手")),
        ["张望"] = new([], [], 3.4, [("FaceAngleX", "0:0 .5:24 .8:22 1.1:25 1.3:24 1.9:-22 2.2:-20 2.5:-23 2.7:-22 3.3:0"), ("FaceAngleY", "0:0 .5:4 1.3:3 1.9:5 2.7:3 3.3:0")]),
        ["哼歌"] = new([], [("EyeSmile", 1), ("EyeOpen", 0), ("ParamMouthForm", 1)], 3.4, [("FaceAngleZ", "0:0 .6:14 1.4:-14 2.2:14 3.2:0"), ("FaceAngleX", "0:0 .6:4 1.4:-4 2.2:4 3.2:0"), ("FaceAngleY", "0:0 .3:3 .6:0 1:3 1.4:0 1.8:3 2.2:0 3.2:0")]),
        ["打哈欠"] = new([], [], 3.6, [("EyeOpen", "0:0 .5:-.5 .9:-1 2.3:-1 2.7:-.45 3.1:-.4 3.6:0"), ("ParamMouthOpenY", "0:0 .5:.4 1:1 2.1:1 2.6:0"), ("JawOpen", "0:0 .5:.3 1:1 2.1:1 2.6:0"), ("BrowInnerUp", "0:0 .8:.9 2.1:.9 2.7:0"), ("FaceAngleY", "0:0 .9:12 2.1:14 2.8:-3 3.6:0"), ("FaceAngleZ", "0:0 .9:7 2.1:9 3.2:0"), ("LeanInput", "0:0 .9:7 2.1:8 3:0"), ("ViewSquash", "0:0 .9:-.025 2.1:-.03 2.6:.015 3.1:0")]),
        ["打瞌睡"] = new([], [], 5.2, [("EyeOpen", "0:0 .8:-.45 1.6:-.7 2.4:-.9 3:-1 3.15:0 3.9:0 4:-1 4.1:0 4.25:-1 4.35:0 5.2:0"), ("FaceAngleY", "0:0 1:-4 2:-9 2.8:-15 3:-17 3.2:5 3.6:0 5.2:0"), ("FaceAngleZ", "0:0 1.5:5 3:9 3.25:0 5.2:0"), ("ViewSquash", "0:0 1.5:.01 3:.025 3.15:-.02 3.4:.01 3.7:0"), ("ViewY", "0:0 3.05:0 3.2:.02 3.45:0"), ("BrowInnerUp", "0:0 3.05:0 3.25:.9 4.3:.6 5.2:0"), ("FaceAngleX", "0:0 3.4:0 3.8:12 4.4:-10 5:0")], Pushed: true),
        ["歪头"] = new([], [("FaceAngleZ", 18), ("FaceAngleX", 5), ("BrowInnerUp", .6), ("ParamBrowLY", .4), ("ParamBrowRY", .4), ("ParamEyeBallY", .2)], 3.2, [("FaceAngleZ", "0:0 .25:4 .5:0"), ("EyeOpen", "0:0 1.1:0 1.2:-1 1.32:0 1.48:-1 1.6:0 3.2:0")], Pushed: true),
        ["发呆"] = new([], [], 4.6, [("EyeOpen", "0:0 .9:-.45 3.1:-.5 3.25:-1 3.4:0 4.6:0"), ("ParamEyeBallY", "0:0 .9:.45 3.1:.45 3.4:0"), ("ParamMouthOpenY", "0:0 1:.3 3.1:.3 3.3:0"), ("JawOpen", "0:0 1:.18 3.1:.18 3.3:0"), ("FaceAngleY", "0:0 1:-3 3.1:-4 3.35:3 3.8:0"), ("FaceAngleX", "0:0 3.2:0 3.35:-7 3.55:6 3.75:-3 4:0"), ("ViewSquash", "0:0 1:.015 3.1:.015 3.35:0")]),
        ["叹气"] = new([], [], 3.6, [("LeanInput", "0:0 .8:5 1.8:-7 2.8:-6 3.5:0"), ("FaceAngleY", "0:0 .8:7 1.8:-9 2.8:-8 3.5:0"), ("ViewSquash", "0:0 .8:-.02 1.2:-.02 1.9:.03 2.8:.025 3.5:0"), ("EyeOpen", "0:0 .8:-.2 1.8:-.6 2.8:-.55 3.5:0"), ("ParamMouthOpenY", "0:0 1.1:0 1.4:.3 2.2:.12 2.6:0"), ("MouthFunnel", "0:0 1.1:0 1.4:.9 2.2:.6 2.6:0"), ("BrowInnerUp", "0:0 1:.6 2.8:.6 3.5:0")]),
        ["玩狗尾草"] = new([], [("ParamEyeBallX", -.7), ("ParamEyeBallY", -.3), ("EyeOpen", .75), ("EyeSmile", 1), ("ParamMouthForm", 1)], 5.4, [("FaceAngleX", "0:0 .5:0 1.2:-10 4.7:-10 5.4:0"), ("FaceAngleZ", "0:0 .5:0 1.2:8 1.9:3 2.6:9 3.3:3 4:9 4.7:6 5.4:0"), ("FaceAngleY", "0:0 .5:0 1.2:-5 4.7:-5 5.4:0")], ("狗尾草切换出来", "狗尾草切回默认手")),
    };
    // What she does by herself when left alone. A hand only comes up as part of one of these: a gesture on
    // its own for no reason looked odd (the owner, 2026-11-02). Late at night she gets sleepy.
    private static readonly string[] IdleMoods = ["张望", "哼歌", "害羞", "得意", "思考", "打哈欠", "打瞌睡", "歪头", "发呆", "叹气", "玩狗尾草"];
    private static readonly HashSet<string> Sleepy = ["打哈欠", "打瞌睡"];
    internal static int IdleWeight(string name, int hour) => Sleepy.Contains(name) && hour is >= 23 or < 6 ? 4 : 1;
    // Dangling from the mouse: dizzy, mouth open in an "ah", brows up, swaying. 落地 when let go.
    private static readonly (string Id, string Keys)[] Dangling = [("FaceAngleZ", "0:0 .6:10 1.8:-10 2.4:0"), ("FaceAngleX", "0:0 .6:-4 1.8:4 2.4:0")];
    private static readonly (string Id, double Value)[] Hanging = [("ParamMouthOpenY", .35), ("JawOpen", .2), ("MouthFunnel", .3), ("BrowInnerUp", .8)];

    private readonly Action<string> send;
    private readonly Func<IReadOnlyCollection<string>> worn;
    private readonly Random random;
    private readonly HashSet<string> activities = [];
    private readonly List<DateTime> clicks = [];
    private Mood? mood;
    private DateTime moodUntil, nextIdle;
    private string? lastIdle;
    private bool dragging, wearingGlasses;
    private DateTime motionUntil;
    private (string On, string Off)? moodGesture;
    internal string? Showing { get; private set; }
    // Her own body language (Body) under the reflexes: a hand she has up stays, and what a
    // reflex held and let go of is hers again once it ends.
    internal Func<bool>? HandBusy { get; init; }
    internal Action? Ended { get; init; }

    internal Behavior(Action<string> send, Func<IReadOnlyCollection<string>> worn, Random random)
    {
        this.send = send; this.worn = worn; this.random = random;
    }

    // A new renderer knows nothing of what the one before showed.
    internal void Resume(DateTime now, int intervalSeconds)
    {
        mood = null; Showing = null; moodGesture = null; dragging = false; wearingGlasses = false; motionUntil = default;
        nextIdle = now.AddSeconds(intervalSeconds);
        UpdateGlasses();
    }

    // side: which way she was pushed, 1 to the viewer's right. Clicked again in the middle of a movement the
    // face changes at once, but a second movement on top of the first would throw her about.
    internal void Show(string name, DateTime now, int side = 1, bool force = false)
    {
        EndMood();
        var next = Moods[name];
        foreach (var expression in next.Expressions) send($"exp {expression} 1");
        foreach (var (id, value) in next.Parameters)
            send(FormattableString.Invariant($"set {id} {(next.Pushed && side < 0 && Sided.Contains(id) ? 0 - value : value)}"));
        mood = next; Showing = name; moodUntil = now.AddSeconds(next.Seconds);
        if (next.Motion == null || (now < motionUntil && !force)) return;
        // Only what turns with the side flips: squashing, leaning, nodding and her eyelids stay as they are.
        foreach (var (id, keys) in next.Motion) send($"anim {id} {(next.Pushed && side < 0 && Sided.Contains(id) ? Mirrored(keys) : keys)}");
        motionUntil = now.AddSeconds(next.Motion.Max(track => Seconds(track.Keys)) * .6);
        if (next.Gesture is { } hand && HandBusy?.Invoke() != true) { moodGesture = hand; send($"motion {hand.On}"); }
    }

    // What turns with the side she was touched on, held as well as moved.
    private static readonly HashSet<string> Sided = ["FaceAngleX", "FaceAngleZ", "ParamEyeBallX", "MouthX"];

    private static double Seconds(string keys) => double.Parse(keys[(keys.LastIndexOf(' ') + 1)..].Split(':')[0], CultureInfo.InvariantCulture);
    private static string Mirrored(string keys) => string.Join(' ', keys.Split(' ').Select(key =>
    {
        var parts = key.Split(':');
        return FormattableString.Invariant($"{parts[0]}:{0 - double.Parse(parts[1], CultureInfo.InvariantCulture)}");
    }));

    private void EndMood()
    {
        if (mood == null) return;
        var keep = worn();
        // What she wears by the owner's choice stays on when a mood that also used it ends.
        foreach (var expression in mood.Expressions) if (!keep.Contains(expression)) send($"exp {expression} 0");
        foreach (var (id, _) in mood.Parameters) send($"clear {id}");
        if (moodGesture is { } hand) { send($"motion {hand.Off}"); moodGesture = null; }
        mood = null; Showing = null;
        Ended?.Invoke();
    }

    // What she wears changes what is under the cursor: with her tail put away the same spot holds only
    // the ends of her sash, and a long skirt covers her legs.
    internal static string Zone(string shape, double x, double y, IReadOnlyCollection<string> worn)
    {
        string zone = BareZone(shape, x, y);
        if (zone == "tail" && worn.Contains("尾巴开关")) return "skirt";
        if (zone == "body" && y > .5 && worn.Contains("裙子切换")) return "skirt";
        return zone;
    }

    private static string BareZone(string shape, double x, double y) => shape switch
    {
        "chibi" => y < .33 ? "head" : y < .53 ? (x is > .27 and < .73 ? "face" : "head")
            : y < .66 && x is < .4 or > .6 ? "hand" : y < .76 ? "skirt" : "body",
        "full" => y < .17 ? "head" : y < .26 ? (x is > .33 and < .67 ? "face" : "head")
            : y is > .46 and < .56 && x is < .26 or > .74 ? "hand" : y is > .42 and < .57 ? "skirt"
            : y >= .57 && x is < .36 or > .64 ? "tail" : "body",
        _ => SmallZone(x, (y * 620 - 60) / 560),
    };

    // Measured on her without the headroom the window keeps above her for jumping.
    private static string SmallZone(double x, double y) => y < .125 ? "head" : y < .215 ? (x is > .3 and < .7 ? "face" : "head")
        : y is > .5 and < .6 && x is < .28 or > .74 ? "hand" : y is > .43 and < .62 ? "skirt"
        : y >= .62 && x is < .36 or > .62 ? "tail" : "body";

    private static string ClickMood(string zone) => zone switch
    {
        "head" => "摸头", "face" => "戳脸", "hand" => "开心", "skirt" => "护裙", "tail" => "震惊", _ => "心虚",
    };

    internal void Click(string zone, DateTime now, int side = 1)
    {
        clicks.RemoveAll(time => now - time > ClickSpan);
        clicks.Add(now);
        if (clicks.Count >= ClicksForBasin) { clicks.Clear(); Show("挨打", now, force: true); return; }
        Show(ClickMood(zone), now, side);
    }

    internal void DragStart()
    {
        EndMood();
        dragging = true;
        send("exp 圈圈眼 1");
        foreach (var (id, value) in Hanging) send(FormattableString.Invariant($"set {id} {value}"));
        foreach (var (id, keys) in Dangling) send($"animloop {id} {keys}");
    }

    // The dizzy eyes stay on through the landing and go with a blink.
    internal void DragEnd(DateTime now)
    {
        if (!dragging) return;
        dragging = false;
        send("animstop");
        foreach (var (id, _) in Hanging) send($"clear {id}");
        Show("落地", now, force: true);
    }

    // While she is reading, watching or writing something (any kind the agent names) she wears her glasses,
    // however long it takes, until it says she is done.
    internal void Activity(string kind, bool active)
    {
        if (active) activities.Add(kind); else activities.Remove(kind);
        UpdateGlasses();
    }

    internal void ClearActivities()
    {
        if (activities.Count == 0) return;
        activities.Clear();
        UpdateGlasses();
    }

    // Glasses worn by choice (dress) count: she does not stack a second pair, and those stay on after her work ends.
    internal void UpdateGlasses()
    {
        var keep = worn();
        bool wanted = activities.Count > 0 && !keep.Contains(Glasses) && !keep.Contains(Monocle);
        if (wanted == wearingGlasses) return;
        wearingGlasses = wanted;
        if (wanted || !keep.Contains(Glasses)) send($"exp {Glasses} {(wanted ? 1 : 0)}");
    }

    internal void Tick(DateTime now, bool speaking, int intervalSeconds)
    {
        if (mood != null && now >= moodUntil) EndMood();
        if (speaking || dragging || mood != null || now < nextIdle) return;
        nextIdle = now.AddSeconds(intervalSeconds);
        // Never the same one twice in a row.
        int hour = now.ToLocalTime().Hour;
        var choices = IdleMoods.Where(name => name != lastIdle).ToArray();
        int pick = random.Next(choices.Sum(name => IdleWeight(name, hour)));
        foreach (var name in choices)
        {
            pick -= IdleWeight(name, hour);
            if (pick >= 0) continue;
            lastIdle = name;
            Show(name, now, random.Next(2) == 0 ? 1 : -1);
            return;
        }
    }

    // How open her mouth is, one value per step of a 16-bit PCM wav; null for anything else.
    internal static float[]? VoiceLevels(byte[] wav, int stepMilliseconds)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return null;
        int channels = 0, rate = 0, bits = 0;
        for (int at = 12; at + 8 <= wav.Length;)
        {
            string chunk = Encoding.ASCII.GetString(wav, at, 4);
            int size = BitConverter.ToInt32(wav, at + 4), start = at + 8;
            if (size < 0) return null;
            if (chunk == "fmt " && start + 16 <= wav.Length)
            {
                channels = BitConverter.ToInt16(wav, start + 2); rate = BitConverter.ToInt32(wav, start + 4); bits = BitConverter.ToInt16(wav, start + 14);
            }
            else if (chunk == "data")
            {
                if (bits != 16 || channels < 1 || rate < 1) return null;
                return Levels(wav, start, Math.Min(size, wav.Length - start), channels, rate, stepMilliseconds);
            }
            at = start + size + (size & 1);
        }
        return null;
    }

    private static float[] Levels(byte[] wav, int start, int size, int channels, int rate, int stepMilliseconds)
    {
        int window = Math.Max(1, rate * stepMilliseconds / 1000) * channels * 2;
        var loudness = new float[size / window];
        for (int step = 0; step < loudness.Length; step++)
        {
            double sum = 0;
            for (int at = start + step * window; at + 1 < start + (step + 1) * window; at += 2)
            {
                double sample = BitConverter.ToInt16(wav, at) / 32768.0;
                sum += sample * sample;
            }
            loudness[step] = (float)Math.Sqrt(sum / (window / 2));
        }
        // Against the loud parts of this recording, so a quiet voice opens her mouth as far as a loud one.
        float peak = loudness.Length == 0 ? 0 : loudness.Order().ElementAt(loudness.Length * 95 / 100);
        if (peak <= 0) return new float[loudness.Length];
        return loudness.Select(level => level / peak < .12f ? 0f : (float)Math.Min(1, Math.Sqrt(level / peak))).ToArray();
    }
}
