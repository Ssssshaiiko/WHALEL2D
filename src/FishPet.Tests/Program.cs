// Her reflexes and her body language, checked without a window or a renderer: what commands they send.
using FishPet;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
VerifyLive2DBehavior();
Console.WriteLine($"FishPet: {checks} checks passed.");

void VerifyLive2DBehavior()
{
    var sent = new List<string>();
    var worn = new List<string>();
    var start = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    var live2d = new Behavior(sent.Add, () => worn, new Random(1));
    live2d.Resume(start, 40);
    bool Said(string command) => sent.Contains(command);

    foreach (var mood in Behavior.Moods)
    {
        Check(mood.Value.Motion is { Length: > 0 } && !mood.Value.Expressions.Contains("加载"), "every mood moves her body: " + mood.Key);
        foreach (var (id, keys) in mood.Value.Motion!)
        {
            var parts = keys.Split(' ');
            Check(parts[0].StartsWith("0:") && parts[^1].EndsWith(":0"), $"{mood.Key} {id} starts and ends at rest");
            var times = parts.Select(key => double.Parse(key.Split(':')[0], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Check(times.Zip(times.Skip(1)).All(pair => pair.First < pair.Second), $"{mood.Key} {id} runs forward");
        }
    }
    // Swinging round her feet looked like a roly-poly (the owner, 2026-11-02): only the head pat he liked keeps a little of it.
    Check(Behavior.Moods.Where(mood => mood.Key != "摸头").All(mood => mood.Value.Motion!.All(track => track.Id is not ("ViewRoll" or "ViewX"))), "no roly-poly swaying");
    // She jumps only when startled, and heart eyes are for love, which none of her reflexes is (the owner, 2026-11-02).
    Check(Behavior.Moods.Where(mood => mood.Value.Motion!.Any(track => track.Id == "ViewY")).Select(mood => mood.Key).Order().SequenceEqual(["打瞌睡", "震惊"]), "jumping only in fright");
    Check(Behavior.Moods.All(mood => !mood.Value.Expressions.Contains("爱心眼")), "no heart eyes without love");
    Check(Behavior.Zone("small", .5, .05, worn) == "head" && Behavior.Zone("small", .5, .24, worn) == "face", "small head and face");
    Check(Behavior.Zone("small", .5, .5, worn) == "skirt" && Behavior.Zone("small", .85, .75, worn) == "tail", "small skirt and tail");
    Check(Behavior.Zone("small", .22, .58, worn) == "hand" && Behavior.Zone("small", .5, .8, worn) == "body", "small hand and legs");
    Check(Behavior.Zone("chibi", .5, .42, worn) == "face" && Behavior.Zone("chibi", .5, .2, worn) == "head", "chibi head and face");
    Check(Behavior.Zone("full", .5, .2, worn) == "face" && Behavior.Zone("full", .1, .7, worn) == "tail", "full body zones");
    Check(Behavior.Zone("small", .85, .75, ["尾巴开关"]) == "skirt" && Behavior.Zone("full", .1, .7, ["尾巴开关"]) == "skirt", "no tail to touch once it is put away");
    Check(Behavior.Zone("small", .5, .8, ["裙子切换"]) == "skirt" && Behavior.Zone("small", .5, .3, ["裙子切换"]) == "body", "a long skirt covers her legs, not her chest");

    live2d.Click("head", start);
    Check(live2d.Showing == "摸头" && Said("exp 脸红 1") && Said("set EyeSmile 0.9"), "stroking her head");
    Check(sent.Any(command => command.StartsWith("anim ViewRoll ")), "her body moves with her face");
    live2d.Tick(start.AddSeconds(1), false, 40);
    Check(live2d.Showing == "摸头", "a mood stays for its time");
    sent.Clear();
    live2d.Tick(start.AddSeconds(3.1), false, 40);
    Check(live2d.Showing == null && Said("exp 脸红 0") && Said("clear EyeSmile"), "a mood ends and leaves nothing on");

    sent.Clear();
    live2d.Click("face", start.AddSeconds(4), 1);
    Check(Said("set FaceAngleX 14") && Said("anim FaceAngleX 0:0 .08:12 .3:6 1.2:0"), "poked from the left she turns away to the right");
    live2d.Click("tail", start.AddSeconds(4.2));
    Check(Said("exp ＞＜ 0") && live2d.Showing == "震惊", "a new reaction replaces the one before");
    Check(!sent.Any(command => command.StartsWith("anim ViewY ")), "movements do not pile up on quick clicks");
    for (int i = 0; i < 3; i++) live2d.Click("body", start.AddSeconds(4.4 + i * .2));
    Check(live2d.Showing == "挨打" && Said("exp 钢盆 1") && Said("anim ViewSquash 0:0 .09:.11 .3:.02 .5:.05 .75:.01 1.4:0"), "clicking too often brings the basin down");
    sent.Clear();
    worn.Add("钢盆");
    live2d.Tick(start.AddSeconds(9), false, 40);
    Check(!Said("exp 钢盆 0") && Said("exp 流冷汗 0"), "what the owner put on her stays after a mood used it");
    worn.Clear();

    sent.Clear();
    live2d.Activity("video", true);
    Check(Said("exp 黑框眼镜 1"), "glasses on while she watches");
    live2d.Show("开心", start.AddSeconds(10));
    Check(!Said("exp 黑框眼镜 0"), "a reaction leaves them on");
    live2d.Activity("writing", true);
    live2d.Activity("video", false);
    Check(!Said("exp 黑框眼镜 0"), "still on while she writes");
    live2d.Activity("writing", false);
    Check(Said("exp 黑框眼镜 0"), "off when everything is done");
    sent.Clear();
    worn.Add("单片眼镜");
    live2d.Activity("share", true);
    Check(!Said("exp 黑框眼镜 1"), "no second pair over the owner's");
    live2d.ClearActivities();
    worn.Clear(); worn.Add("黑框眼镜");
    sent.Clear();
    live2d.Activity("share", true);
    live2d.Activity("share", false);
    Check(sent.Count == 0, "the owner's own pair is never taken off");
    worn.Clear();
    live2d.Activity("bilibili", true);
    sent.Clear();
    live2d.ClearActivities();
    Check(Said("exp 黑框眼镜 0"), "nobody left to say the work ended");

    sent.Clear();
    live2d.Click("skirt", start.AddSeconds(12));
    // The owner found swinging round her feet like a roly-poly the least natural (2026-11-02).
    Check(live2d.Showing == "护裙" && Said("motion 默认手切举手") && !sent.Any(command => command.StartsWith("anim ViewRoll ") || command.StartsWith("anim ViewX ")), "touching her skirt she guards it without swaying");
    live2d.Tick(start.AddSeconds(14), false, 40);
    sent.Clear();
    live2d.DragStart();
    Check(live2d.Showing == null && Said("exp 圈圈眼 1") && Said("set ParamMouthOpenY 0.35") && sent.Any(command => command.StartsWith("animloop ")), "dragged she goes dizzy and dangles");
    live2d.DragEnd(start.AddSeconds(16)); live2d.DragEnd(start.AddSeconds(16.1));
    Check(live2d.Showing == "落地" && sent.Count(command => command.StartsWith("anim ViewSquash ")) == 1 && Said("animstop") && Said("clear JawOpen"), "and lands once");
    Check(!Said("exp 圈圈眼 0"), "still dizzy as she lands");
    live2d.Tick(start.AddSeconds(17.3), false, 40);
    Check(sent.Count(command => command == "exp 圈圈眼 0") == 1, "and blinks it away");
    Check(Behavior.Moods["问候"].Gesture != null && Behavior.Moods["告别"].Motion!.Any(track => track.Id == "EyeOpen"), "a maid's greeting and goodbye");

    var right = new Behavior(sent.Add, () => worn, new Random(3));
    sent.Clear();
    right.Resume(start, 40);
    right.Click("face", start, -1);
    Check(Said("set FaceAngleX -14") && Said("anim FaceAngleX 0:0 .08:-12 .3:-6 1.2:0"), "poked from the right she turns away to the left");
    Check(Said("anim FaceAngleY 0:0 .08:-6 .4:-2 1.2:0") && Said("anim ViewSquash 0:0 .06:.04 .25:0 1.2:0"), "only her turning flips, not her flinch");
    sent.Clear();
    right.Click("hand", start.AddSeconds(10));
    Check(Said("motion 默认手切举手"), "clicked on the hand she raises it");
    right.Tick(start.AddSeconds(14), false, 40);
    Check(Said("motion 举手切默认手"), "and lowers it again");

    var quiet = new Behavior(sent.Add, () => worn, new Random(2));
    sent.Clear();
    quiet.Resume(start, 40);
    quiet.Tick(start.AddSeconds(100), true, 40);
    Check(sent.Count == 0, "no small move while she speaks");
    quiet.Tick(start.AddSeconds(30), false, 40);
    Check(sent.Count == 0, "none before the interval");
    quiet.Tick(start.AddSeconds(41), false, 40);
    Check(sent.Count > 0, "a small move after the interval");

    // A hand lifted on its own for no reason looked odd (the owner, 2026-11-02): only a move that needs it
    // brings one up. She never does the same thing twice in a row, and late at night she gets sleepy.
    var idle = new Behavior(sent.Add, () => worn, new Random(5));
    idle.Resume(start, 40);
    string? previous = null;
    bool repeated = false, strayHand = false, grass = false;
    var done = new HashSet<string>();
    for (int i = 0; i < 300; i++)
    {
        var at = start.AddSeconds(41 + i * 50);
        idle.Tick(at.AddSeconds(-20), false, 40);
        sent.Clear();
        idle.Tick(at, false, 40);
        var name = idle.Showing!;
        repeated |= name == previous;
        previous = name;
        done.Add(name);
        strayHand |= sent.Any(command => command.StartsWith("motion ")) && Behavior.Moods[name].Gesture == null;
        grass |= name == "玩狗尾草" && Said("motion 狗尾草切换出来") && Said("anim FaceAngleX 0:0 .5:0 1.2:-10 4.7:-10 5.4:0");
    }
    Check(done.Count == 11 && !repeated, "all her small moves come round, never one twice in a row");
    Check(!strayHand && grass, "a hand comes up only with a move that uses it");
    Check(Behavior.IdleWeight("打瞌睡", 2) > Behavior.IdleWeight("打瞌睡", 14)
        && Behavior.IdleWeight("张望", 2) == Behavior.IdleWeight("张望", 14), "sleepy late at night only");

    const int rate = 24000, milliseconds = 100;
    var samples = new short[rate];
    for (int i = rate / 2; i < rate; i++) samples[i] = (short)(Math.Sin(i * .1) * 12000);
    var wav = new List<byte>();
    void Add(params byte[] bytes) => wav.AddRange(bytes);
    Add("RIFF"u8.ToArray()); Add(BitConverter.GetBytes(36 + rate * 2)); Add("WAVEfmt "u8.ToArray()); Add(BitConverter.GetBytes(16));
    Add(BitConverter.GetBytes((short)1)); Add(BitConverter.GetBytes((short)1)); Add(BitConverter.GetBytes(rate)); Add(BitConverter.GetBytes(rate * 2));
    Add(BitConverter.GetBytes((short)2)); Add(BitConverter.GetBytes((short)16)); Add("data"u8.ToArray()); Add(BitConverter.GetBytes(rate * 2));
    foreach (var sample in samples) Add(BitConverter.GetBytes(sample));
    var levels = Behavior.VoiceLevels(wav.ToArray(), milliseconds)!;
    Check(levels.Length == 10 && levels[1] == 0 && levels[8] > .9, "her mouth follows the loudness of the recording");
    Check(Behavior.VoiceLevels(new byte[100], milliseconds) == null, "anything but a wav is left alone");

    // Her own body language (the owner reviewed it, 2026-11-02): words in 〔〕, each a hand-tuned piece, stacked
    // by the rules learned on this model, and her words waiting for what her body does.
    var acting = new List<string>();
    var body = new Body(acting.Add);
    bool Did(string command) => acting.Contains(command);
    double waited = body.Apply("歪头:右 眯眼笑 点头");
    Check(Did("set FaceAngleZ -16") && Did("set EyeOpen 0") && Did("set EyeSmile 1"), "a cue becomes her body, directions as the owner sees her");
    Check(Did("anim FaceAngleY 0:0 0.17:-10 0.4:1 0.63:-10 0.86:1 1.06:0") && Math.Abs(waited - 1.06) < 1e-9, "a nod is two, and her words wait for both");
    acting.Clear();
    Check(body.Apply("快点头×3") < 1.06 && acting.Single().Split(":-10").Length == 4, "quicker and counted");
    acting.Clear();
    body.Apply("笑眼 转头:左 看左边");
    Check(Did("set EyeOpen 0.45") && !acting.Any(command => command.StartsWith("set EyeSmile")) && Did("set FaceAngleX 27"), "a new look on the same place replaces the old, looking aside while turned adds up");
    acting.Clear();
    body.Apply("脸红 大笑 微微笑");
    Check(Did("exp 脸红 1") && body.Overlays.Contains("脸红") && Did("set ParamMouthForm 0.5") && !Did("set ParamMouthOpenY 0.6"), "大笑 is a word of its own, 微 makes 微笑 smaller, the later mouth wins");
    acting.Clear();
    body.Apply("吐舌 屑屑眼 -脸红");
    Check(!Did("set TongueOut 1") && Did("exp 屑屑眼 1") && Did("exp 脸红 0"), "the smug eyes bring their own mouth, and a minus takes one thing off");
    acting.Clear();
    Check(body.Apply("抚胸") >= 1.1 && Did("motion 默认手切举手") && body.HandUp, "her hand comes up and she waits for it");
    body.Apply("竖手指");
    Check(acting.SkipWhile(command => command != "motion 举手切默认手").Contains("motion 竖手指切出来动画"), "one hand at a time");
    acting.Clear();
    body.Apply("叉腰");
    Check(acting.Count == 0 && body.Unknown.Contains("叉腰"), "what her body cannot do is left out and noted");
    body.Apply("歪头:右");
    acting.Clear();
    body.Restore();
    Check(Did("set FaceAngleZ -16"), "after a reflex her own look comes back");
    acting.Clear();
    body.Apply("恢复");
    Check(!body.Posing && Did("clear FaceAngleZ") && Did("exp 屑屑眼 0") && Did("motion 竖手指切回默认") && !acting.Any(command => command.StartsWith("exp 体型")),
        "back to how she is, leaving her body type alone");
    Check(Body.Strip("〔点头〕好呀〔微笑〕") == "好呀" && !Body.HasCues("好呀"), "the words without her cues");

    // Her moves go with her words (the owner, 2026-11-02: "动作要配合语气和句子长短").
    acting.Clear();
    Check(body.Apply("点头", 3) == .35 && acting.Single().EndsWith(" 1.696:0"), "a long sentence: the nod stretches over it and the words go on as it starts");
    acting.Clear();
    body.Apply("点头", .3);
    Check(acting.Single().EndsWith(" 0.742:0"), "a short one: quicker");
    acting.Clear();
    body.Apply("点头", 1.06, '！');
    Check(acting.Single().Contains(":-12 "), "said with a ！: bigger");
    acting.Clear();
    Check(body.Apply("吓一跳", 3) == .45 && acting.Any(command => command == "anim ViewY 0:0 0.06:0 0.2:0.06 0.4:0"), "a fright keeps its own speed");
    acting.Clear();
    body.Apply("慢点头", 3);
    Check(acting.Single().EndsWith(" 1.802:0"), "what she says of the speed herself wins");
    body.Apply("恢复");
    var heard = new List<(double Spoken, char Tone)>();
    var shouted = new Line("〔点头〕好呀！〔微笑〕嗯……", (cue, spoken, tone) => { heard.Add((spoken, tone)); return .35; });
    shouted.Advance(10);
    Check(heard.Count == 2 && Math.Abs(heard[0].Spoken - (2 / 9.0 + .12)) < 1e-9 && heard[0].Tone == '！' && heard[1].Tone == '…', "each cue knows the words it goes with");

    var cues = new List<string>();
    var said = new Line("〔点头〕好呀……嗯", (cue, _, _) => { cues.Add(cue); return 1; });
    said.Advance(0);
    Check(cues.SequenceEqual(["点头"]) && said.Shown == 0 && !said.Talking, "she acts before she speaks");
    said.Advance(.5);
    Check(said.Shown == 0, "and her words wait for it");
    said.Advance(1.05);
    Check(said.Plain == "好呀……嗯" && said.Shown == 1 && said.Talking, "then the words come at a speaking pace");
    said.Advance(1.3);
    Check(said.Shown == 3 && !said.Talking, "her mouth rests on the dots");
    said.Advance(5);
    Check(said.Done && said.Shown == said.Plain.Length, "all said");
    var spoken = new Line("〔点头〕好呀", (_, _, _) => 1, voiceSeconds: 2);
    spoken.Advance(1.5);
    Check(spoken.Shown == 2 && !spoken.Done, "with her voice the words spread over the recording and nothing waits");
    spoken.Advance(2.1);
    Check(spoken.Done, "and end with it");
    // What she says at once is a bubble a line, cues alone on a line going with the words after them.
    Check(Line.Split("〔看过来〕\n好呀〔点头〕\r\n\n  明天见  \n〔眯眼笑〕").SequenceEqual(["〔看过来〕好呀〔点头〕", "明天见〔眯眼笑〕"]), "several lines are several bubbles");
    Check(Line.Split("好呀").SequenceEqual(["好呀"]) && Line.Split("〔点头〕").SequenceEqual(["〔点头〕"]), "one line, or cues alone, is one");

    var restored = 0;
    var underneath = new Behavior(sent.Add, () => worn, new Random(4)) { HandBusy = () => true, Ended = () => restored++ };
    underneath.Resume(start, 40);
    sent.Clear();
    underneath.Click("hand", start);
    Check(underneath.Showing == "开心" && !Said("motion 默认手切举手"), "a reflex leaves the hand she has up");
    underneath.Tick(start.AddSeconds(3.1), false, 40);
    Check(restored == 1, "and gives her look back when it ends");
}
