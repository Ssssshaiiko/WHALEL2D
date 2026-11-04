using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FishPet;

// Whether the plugin's GitHub repository has moved on from this copy (the owner, 2026-11-03: checked when she starts,
// told to the agent with what changed, and the user decides). Read-only, through the GitHub API, so git need not be
// installed; nothing about the user goes out. A copy that is not a git clone of a GitHub repository is not checked.
internal sealed partial class Updates(string root, Action<string> log)
{
    // Behind: how many changes the repository has that this copy does not; Changes: what each is, newest first;
    // Guide: whether the body guide is among them, which the agent has copied into its persona and must put in anew.
    internal sealed record Found(string Current, string Latest, int Behind, string[] Changes, bool Guide = false);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private string? told;
    internal Found? Latest { get; set; }

    internal async Task Check()
    {
        try
        {
            if (Here() is not { } here) return;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{here.Repository}/compare/{here.Commit}...{here.Branch}");
            request.Headers.UserAgent.ParseAdd("FishPet");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) { log("查更新：GitHub 回了 " + (int)response.StatusCode); return; }
            using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            int behind = answer.RootElement.GetProperty("ahead_by").GetInt32();
            if (behind == 0) { Latest = null; return; }
            var commits = answer.RootElement.GetProperty("commits").EnumerateArray().ToArray();
            var changes = Enumerable.Reverse(commits).Take(10).Select(commit => commit.GetProperty("commit").GetProperty("message").GetString()!.Split('\n')[0].Trim())
                .Select(line => line.Length > 60 ? line[..60] + "…" : line).ToArray();
            Latest = new Found(here.Commit[..7], commits[^1].GetProperty("sha").GetString()![..7], behind, changes, TouchesGuide(answer.RootElement));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        { log("查更新没查成：" + error.Message); }
    }

    // Whether the changes include the body guide (GitHub's compare lists the files changed).
    internal static bool TouchesGuide(JsonElement compare) => compare.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array &&
        files.EnumerateArray().Any(file => file.TryGetProperty("filename", out var name) && name.GetString() == "prompt/身体说明.md");

    // The update, the first time it is asked for after it was found, and again each day it is still not taken (the owner,
    // 2026-11-05: told once, an agent that let it pass never said it again, however long its session ran).
    internal static readonly TimeSpan Again = TimeSpan.FromHours(24);
    private DateTime toldAt;
    internal Found? Unannounced() => Unannounced(DateTime.UtcNow);
    internal Found? Unannounced(DateTime now)
    {
        if (Latest is not { } found || (found.Latest == told && now - toldAt < Again)) return null;
        told = found.Latest; toldAt = now;
        return found;
    }

    // This copy's commit and branch, and its GitHub repository (owner/name) from the origin remote.
    private (string Commit, string Branch, string Repository)? Here()
    {
        var git = Path.Combine(root, ".git");
        if (File.Exists(git) && File.ReadAllText(git).Trim() is var pointer && pointer.StartsWith("gitdir:"))
            git = Path.GetFullPath(Path.Combine(root, pointer["gitdir:".Length..].Trim()));
        if (!Directory.Exists(git) || !File.Exists(Path.Combine(git, "HEAD")) || !File.Exists(Path.Combine(git, "config"))) return null;
        var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
        string branch = "main", commit = head;
        if (head.StartsWith("ref: "))
        {
            var reference = head[5..].Trim();
            branch = reference.StartsWith("refs/heads/") ? reference["refs/heads/".Length..] : branch;
            var loose = Path.Combine(git, reference.Replace('/', Path.DirectorySeparatorChar));
            var packed = Path.Combine(git, "packed-refs");
            commit = File.Exists(loose) ? File.ReadAllText(loose).Trim()
                : File.Exists(packed) ? File.ReadLines(packed).Select(line => line.Split(' ')).FirstOrDefault(parts => parts.Length == 2 && parts[1] == reference)?[0] ?? "" : "";
        }
        if (!CommitPattern().IsMatch(commit)) return null;
        var origin = Regex.Match(File.ReadAllText(Path.Combine(git, "config")), @"\[remote ""origin""\][^\[]*?url\s*=\s*(\S+)");
        var repository = origin.Success ? GitHubPattern().Match(origin.Groups[1].Value) : Match.Empty;
        return repository.Success ? (commit, branch, repository.Groups[1].Value) : null;
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitPattern();
    [GeneratedRegex(@"github\.com[:/]([^/\s]+/[^/\s]+?)(?:\.git)?/?$")]
    private static partial Regex GitHubPattern();
}
