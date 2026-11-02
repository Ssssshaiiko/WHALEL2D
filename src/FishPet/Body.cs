using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FishPet;

// Her own body language on the Live2D display: the words she puts in 〔〕 in what she says, each made of a
// hand-tuned piece, stacked so that whatever she writes keeps to what looked right on this model (the owner
// reviewed it, 2026-11-02). Held words set a channel, and a later word on the same channel takes its place;
// moves play once on top. Directions are as the owner sees the screen: positive FaceAngleX/Z and eye X turn
// to the screen's left. Knows nothing of windows, so it is checked in FishPet.Tests.
internal sealed partial class Body
{
    private sealed record Word(string? Channel = null, (string Id, double Value)[]? Hold = null, string? Overlay = null,
        (string On, string Off)? Hand = null, bool HandDown = false, (string Id, string Keys)[]? Move = null, string[]? Clear = null, bool Reset = false);

    private const double LongestWait = 2.4;
    private static readonly (string On, string Off) HandToChest = ("默认手切举手", "举手切默认手"), FingerUp = ("竖手指切出来动画", "竖手指切回默认"),
        Grass = ("狗尾草切换出来", "狗尾草切回默认手");

    private static int Side(string? where) => where == "右" ? -1 : 1;
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Scale(string keys, double by) => Rework(keys, (time, value) => (time, value * by));
    private static string Pace(string keys, double by) => Rework(keys, (time, value) => (time * by, value));
    private static string Rework(string keys, Func<double, double, (double, double)> change) => string.Join(' ', keys.Split(' ').Select(key =>
    {
        var parts = key.Split(':');
        var (time, value) = change(double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
        return Number(time) + ":" + Number(value);
    }));
    private static double End(string keys) => double.Parse(keys[(keys.LastIndexOf(' ') + 1)..].Split(':')[0], CultureInfo.InvariantCulture);
    // A run of the same small move: down and back each time, then still.
    private static string Repeat(int times, double each, double down, double back, double downValue, double backValue, double rest)
    {
        var keys = new List<string> { "0:0" };
        double at = 0;
        for (int i = 0; i < times; i++) { keys.Add(Number(at + down) + ":" + Number(downValue)); keys.Add(Number(at + back) + ":" + Number(backValue)); at += each; }
        keys.Add(Number(at + rest) + ":0");
        return string.Join(' ', keys);
    }

    private static Word Look(string where, double s) => where switch
    {
        "上" => new("gaze", [("ParamEyeBallY", .6 * s), ("FaceAngleY", 4 * s)]),
        "下" => new("gaze", [("ParamEyeBallY", -.6 * s), ("FaceAngleY", -5 * s)]),
        _ => new("gaze", [("ParamEyeBallX", .75 * s * Side(where)), ("FaceAngleX", 7 * s * Side(where))]),
    };

    // A nod or shake is two unless she says otherwise: one alone was over before it showed (the owner, 2026-11-02).
    private static readonly Dictionary<string, Func<string?, double, int?, Word>> Words = new()
    {
        ["歪头"] = (a, s, _) => new("tilt", [("FaceAngleZ", 16 * s * Side(a))]),
        ["转头"] = (a, s, _) => new("turn", [("FaceAngleX", 20 * s * Side(a))]),
        ["低头"] = (_, s, _) => new("pitch", [("FaceAngleY", -14 * s)]),
        ["抬头"] = (_, s, _) => new("pitch", [("FaceAngleY", 12 * s)]),
        ["正视"] = (_, _, _) => new(Clear: ["tilt", "turn", "pitch", "gaze"]),
        ["前倾"] = (_, s, _) => new("lean", [("LeanInput", -14 * s)]),
        ["后仰"] = (_, s, _) => new("lean", [("LeanInput", 9 * s)]),
        ["站直"] = (_, _, _) => new(Clear: ["lean"]),
        // Her eyes lead and her head follows a little, the way people look aside.
        ["看过来"] = (_, _, _) => new(Clear: ["gaze"]), ["看你"] = (_, _, _) => new(Clear: ["gaze"]),
        ["看左边"] = (_, s, _) => Look("左", s), ["看右边"] = (_, s, _) => Look("右", s),
        ["看上面"] = (_, s, _) => Look("上", s), ["看下面"] = (_, s, _) => Look("下", s),
        ["睁大"] = (_, s, _) => new("eyes", [("EyeOpen", 1 + .35 * s)]),
        ["半睁"] = (_, s, _) => new("eyes", [("EyeOpen", 1 - .55 * s)]),
        ["闭眼"] = (_, _, _) => new("eyes", [("EyeOpen", 0)]),
        // EyeSmile shows only on closing lids.
        ["眯眼笑"] = (_, _, _) => new("eyes", [("EyeOpen", 0), ("EyeSmile", 1)]),
        ["笑眼"] = (_, _, _) => new("eyes", [("EyeOpen", .45), ("EyeSmile", 1)]),
        ["斜眼"] = (_, s, _) => new("eyes", [("REyeSquint", .9 * s), ("LEyeSquint", .9 * s), ("EyeOpen", .7)]),
        ["眨单眼"] = (_, _, _) => new("eyes", [("ParamEyeLOpen", 0), ("EyeSmile", 1)]),
        ["挑眉"] = (_, s, _) => new("brows", [("ParamBrowLY", .6 * s), ("ParamBrowRY", .6 * s), ("BrowInnerUp", .4 * s)]),
        ["皱眉"] = (_, s, _) => new("brows", [("ParamBrowLY", -.8 * s), ("ParamBrowRY", -.8 * s), ("ParamBrowLAngle", -s), ("ParamBrowRAngle", -s)]),
        ["八字眉"] = (_, s, _) => new("brows", [("BrowInnerUp", s), ("ParamBrowLAngle", .6 * s), ("ParamBrowRAngle", .6 * s)]),
        ["微笑"] = (_, s, _) => new("mouth", [("ParamMouthForm", s)]),
        ["大笑"] = (_, s, _) => new("mouth", [("ParamMouthForm", 1), ("ParamMouthOpenY", .6 * s), ("JawOpen", .3 * s)]),
        ["坏笑"] = (a, s, _) => new("mouth", [("ParamMouthForm", 1), ("MouthX", .6 * s * Side(a))]),
        ["瘪嘴"] = (_, s, _) => new("mouth", [("ParamMouthForm", -s)]),
        ["张嘴"] = (_, s, _) => new("mouth", [("ParamMouthOpenY", .7 * s), ("JawOpen", .4 * s), ("MouthFunnel", .3)]),
        ["嘟嘴"] = (_, _, _) => new("mouth", [("MouthFunnel", .7), ("Mouthpucker", 1), ("ParamMouthForm", -.4)]),
        ["鼓腮"] = (_, _, _) => new("mouth", [("CheeckPuff", 1)]),
        ["抿嘴"] = (_, _, _) => new("mouth", [("MouthPressLipOpen", -1), ("ParamMouthForm", -.5)]),
        ["吐舌"] = (_, _, _) => new("mouth", [("TongueOut", 1), ("ParamMouthOpenY", .35)]),
        ["脸红"] = (_, _, _) => new(Overlay: "脸红"), ["黑脸"] = (_, _, _) => new(Overlay: "黑脸"),
        ["冷汗"] = (_, _, _) => new(Overlay: "流冷汗"), ["眼泪"] = (_, _, _) => new(Overlay: "流泪"),
        ["爱心眼"] = (_, _, _) => new(Overlay: "爱心眼"), ["圈圈眼"] = (_, _, _) => new(Overlay: "圈圈眼"),
        ["＞＜"] = (_, _, _) => new(Overlay: "＞＜"), ["><"] = (_, _, _) => new(Overlay: "＞＜"), ["屑屑眼"] = (_, _, _) => new(Overlay: "屑屑眼"),
        ["抚胸"] = (_, _, _) => new(Hand: HandToChest), ["竖手指"] = (_, _, _) => new(Hand: FingerUp),
        ["狗尾草"] = (_, _, _) => new(Hand: Grass), ["放下手"] = (_, _, _) => new(HandDown: true),
        // Small things that come and go: how many and how fast is most of what they say (the owner, 2026-11-02).
        ["点头"] = (_, s, n) => new(Move: [("FaceAngleY", Scale(Repeat(n ?? 2, .46, .17, .4, -10, 1, .14), s))]),
        ["摇头"] = (_, s, n) => new(Move: [("FaceAngleX", Scale(Repeat(n ?? 2, .56, .17, .45, -11, 11, .22), s))]),
        ["眨眼"] = (_, _, n) => new(Move: [("EyeOpen", Repeat(n ?? 1, .22, .07, .16, -1, 0, 0))]),
        ["瞟一眼"] = (a, s, _) => new(Move: a is "上" or "下" ? [("ParamEyeBallY", Scale("0:0 .12:.7 .55:.7 .8:0", s * (a == "上" ? 1 : -1)))]
            : [("ParamEyeBallX", Scale("0:0 .12:.85 .55:.85 .8:0", s * Side(a)))]),
        ["挑一下眉"] = (_, s, _) => new(Move: [("ParamBrowLY", Scale("0:0 .1:.7 .45:.7 .7:0", s)), ("ParamBrowRY", Scale("0:0 .1:.7 .45:.7 .7:0", s)),
            ("BrowInnerUp", Scale("0:0 .1:.5 .45:.5 .7:0", s))]),
        ["歪一下头"] = (a, s, _) => new(Move: [("FaceAngleZ", Scale("0:0 .2:12 .6:12 .9:0", s * Side(a)))]),
        ["晃头"] = (_, s, _) => new(Move: [("FaceAngleZ", Scale("0:0 .3:-10 .7:10 1.1:-8 1.5:6 2:0", s))]),
        ["缩一下"] = (_, s, _) => new(Move: [("ViewSquash", Scale("0:0 .08:.05 .3:.01 .6:0", s)), ("FaceAngleY", Scale("0:0 .08:-8 .6:0", s))]),
        ["叹气"] = (_, _, _) => new(Move: [("LeanInput", "0:0 .8:5 1.8:-7 2.8:-6 3.5:0"), ("FaceAngleY", "0:0 .8:7 1.8:-9 2.8:-8 3.5:0"),
            ("ViewSquash", "0:0 .8:-.02 1.2:-.02 1.9:.03 2.8:.025 3.5:0"), ("EyeOpen", "0:0 .8:-.2 1.8:-.6 2.8:-.55 3.5:0"),
            ("MouthFunnel", "0:0 1.1:0 1.4:.9 2.2:.6 2.6:0"), ("ParamMouthOpenY", "0:0 1.1:0 1.4:.3 2.2:.12 2.6:0")]),
        // A jump is for being startled only (the owner, 2026-11-02): the same one as when her tail is touched.
        ["吓一跳"] = (_, s, _) => new(Move: [("ViewY", Scale("0:0 .06:0 .2:.06 .4:0", s)), ("ViewSquash", Scale("0:0 .06:.06 .18:-.05 .4:.05 .6:0", s)),
            ("EyeOpen", Scale("0:0 .08:.35 .8:.3 1.2:0", s)), ("BrowInnerUp", Scale("0:0 .08:.9 1:.6 1.4:0", s))]),
        ["恢复"] = (_, _, _) => new(Reset: true),
    };

    private readonly Action<string> send;
    private readonly Dictionary<string, (string Id, double Value)[]> channels = [];
    private readonly HashSet<string> overlays = [];
    private Dictionary<string, double> shown = [];
    private (string On, string Off)? hand;

    internal Body(Action<string> send) => this.send = send;

    // What she wears this way stays on when one of her reflexes, which also used it, ends.
    internal IReadOnlyCollection<string> Overlays => overlays;
    internal bool HandUp => hand != null;
    internal bool Posing => channels.Count > 0 || overlays.Count > 0 || hand != null;
    // Words her body could not do, for telling her so in her own terms.
    internal List<string> Unknown { get; } = [];

    [GeneratedRegex("〔[^〔〕]*〕")]
    private static partial Regex CuePattern();
    internal static string Strip(string text) => CuePattern().Replace(text, "");
    internal static bool HasCues(string text) => CuePattern().IsMatch(text);
    // What she says in order, each piece either words or a cue (without its brackets).
    internal static List<(bool Cue, string Text)> Parts(string text)
    {
        var parts = new List<(bool, string)>();
        int at = 0;
        foreach (Match match in CuePattern().Matches(text))
        {
            if (match.Index > at) parts.Add((false, text[at..match.Index]));
            parts.Add((true, match.Value[1..^1]));
            at = match.Index + match.Length;
        }
        if (at < text.Length) parts.Add((false, text[at..]));
        return parts;
    }

    // Moves that are reflexes keep their own speed whatever she is saying.
    private static readonly HashSet<string> Reflexes = ["吓一跳", "眨眼", "缩一下"];
    private const double Beat = .35, HandBeat = .7, StartleBeat = .45;

    // Plays one cue and says how long her words wait before going on. Her moves go with her words (tuned by eye,
    // 2026-11-02: "动作要配合语气和句子长短"): stretched or quickened to the words they come with (spoken: how long
    // those take, 0 for none), bigger and quicker before a "！", smaller and slower before "……" (tone), unless she
    // says 微/大/快/慢 herself. Her words wait only for it to begin, a hand to come up, a jump to land; with
    // nothing to say after it, for all of it.
    internal double Apply(string cue, double spoken = 0, char tone = '\0')
    {
        double wait = 0;
        double toneSize = tone == '！' ? 1.2 : tone == '…' ? .85 : 1, toneSpeed = tone == '！' ? .85 : tone == '…' ? 1.25 : 1;
        foreach (var token in cue.Split([' ', '　', '、', '，', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token[0] is '-' or '－')
            {
                if (Find(token[1..]) is not { } gone) { Unknown.Add(token); continue; }
                if (gone.Overlay is { } face && overlays.Remove(face)) send($"exp {face} 0");
                if (gone.Channel is { } place) channels.Remove(place);
                if (gone.Hand is { } off && hand == off) PutHandDown();
                continue;
            }
            var match = Regex.Match(token, @"^(.+?)(?:[×xX*](\d))?(?:[:：](.+))?$");
            string name = match.Groups[1].Value;
            double? size = null, speed = null;
            // 微/大 in front make a word smaller or bigger, 快/慢 a move quicker or slower, unless they belong to it (大笑).
            while (!Words.ContainsKey(name) && name.Length > 1 && name[0] is '微' or '大' or '快' or '慢')
            {
                switch (name[0]) { case '微': size = .5; break; case '大': size = 1.4; break; case '快': speed = .65; break; default: speed = 1.7; break; }
                name = name[1..];
            }
            if (!Words.TryGetValue(name, out var make)) { Unknown.Add(token); continue; }
            int? count = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : null;
            string? where = match.Groups[3].Success ? match.Groups[3].Value : null;
            bool reflex = Reflexes.Contains(name);
            var word = make(where, size ?? 1, count);
            if (word.Move != null && size == null && !reflex) word = make(where, toneSize, count);
            var move = word.Move?.Select(track => (track.Id, track.Keys)).ToArray();
            if (move != null && !reflex)
            {
                double length = move.Max(track => End(track.Keys));
                double pace = speed ?? toneSpeed * (spoken > 0 ? Math.Clamp(spoken / length, .7, 1.6) : 1);
                pace = Math.Clamp(pace, .6, 1.8);
                if (pace != 1) move = move.Select(track => (track.Id, Pace(track.Keys, pace))).ToArray();
            }
            else if (move != null && speed is { } quick) move = move.Select(track => (track.Id, Pace(track.Keys, quick))).ToArray();
            double moving = move?.Max(track => End(track.Keys)) ?? 0;
            wait = Math.Max(wait, spoken <= 0 ? Math.Max(.8, Math.Max(moving, word.Hand != null ? 1.1 : 0))
                : name == "吓一跳" ? StartleBeat : word.Hand != null ? HandBeat : Beat);
            if (word.Reset) Relax();
            foreach (var place in word.Clear ?? []) channels.Remove(place);
            if (word.Channel is { } channel) channels[channel] = word.Hold!;
            if (word.Overlay is { } overlay && overlays.Add(overlay)) send($"exp {overlay} 1");
            if (word.HandDown) PutHandDown();
            if (word.Hand is { } up && hand != up) { PutHandDown(); hand = up; send($"motion {up.On}"); }
            foreach (var (id, keys) in move ?? []) send($"anim {id} {keys}");
        }
        // The smug eyes bring their own little mouth: nothing else goes on it (a tongue would float outside).
        if (overlays.Contains("屑屑眼")) channels.Remove("mouth");
        Show();
        return Math.Min(wait, LongestWait);
    }

    private static Word? Find(string name) => Words.TryGetValue(name, out var make) ? make(null, 1, null) : null;

    private void PutHandDown()
    {
        if (hand is { } up) send($"motion {up.Off}");
        hand = null;
    }

    // Channels add up where they touch the same input: looking aside while turned turns a bit further.
    private void Show()
    {
        var total = new Dictionary<string, double>();
        foreach (var hold in channels.Values)
            foreach (var (id, value) in hold) total[id] = total.GetValueOrDefault(id) + value;
        foreach (var id in shown.Keys.Where(id => !total.ContainsKey(id))) send($"clear {id}");
        foreach (var (id, value) in total) if (!shown.TryGetValue(id, out var before) || before != value) send($"set {id} {Number(value)}");
        shown = total;
    }

    // Back to how she is when she writes nothing. Only her own: her body type and what the owner dressed her in stay.
    internal void Relax()
    {
        channels.Clear();
        foreach (var overlay in overlays) send($"exp {overlay} 0");
        overlays.Clear();
        PutHandDown();
        Show();
    }

    // After one of her reflexes, which held some of the same parameters and let them go.
    internal void Restore()
    {
        foreach (var (id, value) in shown) send($"set {id} {Number(value)}");
    }

    // A new renderer knows nothing of what the one before showed.
    internal void Forget()
    {
        channels.Clear(); overlays.Clear(); shown = []; hand = null;
    }
}

// One thing she says, acted out: the words come at a speaking pace, and each 〔〕 holds them until her body has
// shown it, so what she says and does stay together. With her recorded voice the words spread over its length
// instead and nothing waits: a recording cannot pause.
internal sealed class Line
{
    internal const double CharactersPerSecond = 9;
    private const double Dots = .25, Pause = .12;
    private const string Stops = "，。！？、,!?;；：:~～—";
    private readonly List<(string? Cue, int End, double Seconds, bool Talks)> steps = [];
    // act(cue, spoken, tone): spoken is how long the words after it take, tone their "！" or "…" (Body.Apply).
    private readonly Func<string, double, char, double> act;
    private readonly bool recorded;
    private int index;
    private double at;

    internal Line(string text, Func<string, double, char, double> act, double? voiceSeconds = null)
    {
        this.act = act;
        recorded = voiceSeconds is > 0;
        var plain = new System.Text.StringBuilder();
        foreach (var (cue, words) in Body.Parts(text))
        {
            if (cue) { steps.Add((words, plain.Length, 0, false)); continue; }
            var letters = StringInfo.GetTextElementEnumerator(words);
            while (letters.MoveNext())
            {
                var letter = letters.GetTextElement();
                plain.Append(letter);
                bool dots = letter is "…" or "⋯" or ".", stop = Stops.Contains(letter, StringComparison.Ordinal), blank = string.IsNullOrWhiteSpace(letter);
                steps.Add((null, plain.Length, blank ? 0 : dots ? Dots : stop ? Pause : 1 / CharactersPerSecond, !blank && !dots && !stop));
            }
        }
        Plain = plain.ToString();
        double total = steps.Sum(step => step.Seconds);
        if (recorded && total > 0)
            for (int i = 0; i < steps.Count; i++) steps[i] = steps[i] with { Seconds = steps[i].Seconds * voiceSeconds!.Value / total };
    }

    // The words between a cue and the next: how long they take to say, and whether they are said with a "！" or trail off.
    private (double Spoken, char Tone) After(int cue)
    {
        double spoken = 0;
        int from = steps[cue].End, to = from;
        for (int i = cue + 1; i < steps.Count && steps[i].Cue == null; i++) { spoken += steps[i].Seconds; to = steps[i].End; }
        var words = Plain[from..to];
        char tone = words.Contains('！') || words.Contains('!') ? '！' : words.TrimEnd().EndsWith('…') || words.TrimEnd().EndsWith('⋯') ? '…' : '\0';
        return (spoken, tone);
    }

    internal string Plain { get; }
    // How much of Plain is out, in UTF-16 units.
    internal int Shown { get; private set; }
    internal bool Talking { get; private set; }
    internal bool Done => index >= steps.Count;
    // When all of it, the last wait included, will have played (seconds since the line began).
    internal double Until => at;

    // seconds: since the line began.
    internal void Advance(double seconds)
    {
        Talking = false;
        while (index < steps.Count && at <= seconds)
        {
            var step = steps[index];
            if (step.Cue is { } cue)
            {
                var (spoken, tone) = After(index);
                double wait = act(cue, spoken, tone);
                if (!recorded) at += wait;
                index++;
                continue;
            }
            Shown = step.End;
            if (seconds < at + step.Seconds) { Talking = step.Talks; return; }
            at += step.Seconds;
            index++;
        }
    }
}
