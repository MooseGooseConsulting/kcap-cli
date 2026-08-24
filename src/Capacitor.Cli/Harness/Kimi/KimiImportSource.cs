using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.Kimi;

/// <summary>
/// Discovers and imports Kimi Code's root wire streams. Kimi child-agent wires
/// are retained as discovery metadata only; no historical child delivery exists.
/// </summary>
internal sealed class KimiImportSource : IImportSource {
    readonly string _home;
    readonly Func<string, Task<RepositoryPayload?>> _repoDetector;

    public KimiImportSource(string? homeOverride = null, Func<string, Task<RepositoryPayload?>>? repoDetector = null) {
        _home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _repoDetector = repoDetector ?? (cwd => RepositoryDetection.DetectRepositoryAsync(cwd, detectPullRequest: false));
    }

    string KimiCodeSessions => Path.Combine(_home, ".kimi-code", "sessions");
    string KimiSessions => Path.Combine(_home, ".kimi", "sessions");
    public string Vendor => "kimi";
    public bool IsAvailable => Directory.Exists(KimiCodeSessions) || Directory.Exists(KimiSessions);
    public bool SupportsTitleGeneration => false;
    public bool AttachesChildContentOnReplay => false;

    public async Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var sessionFilter = filters.FilterSession is { } sf ? ImportCommand.NormalizeGuid(sf) : null;
        var normalizedCwd = filters.FilterCwd is { } cwd ? NormalizePath(cwd) : null;
        var sinceUtc = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero) : (DateTimeOffset?)null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DiscoveredSession>();

        foreach (var candidate in RootWires(KimiCodeSessions, true).Concat(RootWires(KimiSessions, false))) {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParse(candidate.DashedId, out var guid)) continue;
            var sessionId = guid.ToString("N");
            if (!seen.Add(sessionId) || (sessionFilter is not null && !string.Equals(sessionId, sessionFilter, StringComparison.Ordinal))) continue;
            var info = await ReadWireInfoAsync(candidate.Path, ct);
            if (normalizedCwd is not null && (info.Cwd is null || !PathEquals(NormalizePath(info.Cwd), normalizedCwd))) continue;
            var first = info.FirstTimestamp ?? FileTime(candidate.Path, true);
            if (sinceUtc is { } cutoff && first is { } timestamp && timestamp < cutoff) continue;
            result.Add(new DiscoveredSession(sessionId, Vendor, info.Cwd, first, new Dictionary<string, object?> {
                ["TranscriptPath"] = candidate.Path,
                ["ChildTranscriptPaths"] = ChildWires(candidate.Path, candidate.KimiCode).ToArray(),
                ["Cwd"] = info.Cwd,
                ["Model"] = info.Model,
                ["LastTimestamp"] = info.LastTimestamp,
            }));
        }
        return result;
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) {
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);
        var repoCache = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var s in sessions) {
            var path = (string)s.SourceMeta!["TranscriptPath"]!;
            var meta = new SessionMetadata { SessionId = s.SessionId, Cwd = s.Cwd, FirstTimestamp = s.FirstTimestamp,
                LastTimestamp = s.SourceMeta.TryGetValue("LastTimestamp", out var last) ? last as DateTimeOffset? : null };
            int? lastLine; int count;
            try { (lastLine, count) = await ReadTranscriptStatsAsync(path, ct); }
            catch { results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "transcript read failed")); continue; }
            if (lastLine is null) { results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "empty transcript")); continue; }
            if (count < ctx.MinLines) { results.Add(Make(s, meta, ImportCommand.ClassificationStatus.TooShort, count)); continue; }
            int? serverLast;
            try { serverLast = await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, ct); }
            catch { results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, count, "watermark probe failed")); continue; }
            meta.LastTimestamp ??= FileTime(path, false);
            string? repoKey = null;
            if (ctx.ExcludedRepos is { Count: > 0 } && s.Cwd is { } cwd) {
                if (!repoCache.TryGetValue(cwd, out repoKey)) {
                    try { var repo = await _repoDetector(cwd); repoKey = repo is { Owner: { } o, RepoName: { } n } ? $"{o}/{n}" : null; } catch { }
                    repoCache[cwd] = repoKey;
                }
            }
            var (excludedRepo, excludedPath) = ResolveExclusions(s.Cwd, repoKey, ctx);
            var status = ImportCommand.ClassificationStatus.New;
            var resume = 0;
            if (serverLast is { } watermark) {
                if (watermark >= lastLine.Value) status = ImportCommand.ClassificationStatus.AlreadyLoaded;
                else { status = ImportCommand.ClassificationStatus.Partial; resume = watermark + 1; }
            }
            results.Add(new ImportCommand.SessionClassification { SessionId=s.SessionId, FilePath="", EncodedCwd="", Meta=meta, Status=status,
                Vendor=Vendor, ResumeFromLine=resume, ExcludedRepoKey=excludedRepo, ExcludedPathKey=excludedPath, TotalLines=count, SourceMeta=s.SourceMeta });
        }
        return results;
    }

    public async Task<ImportSessionResult> ImportSessionAsync(ImportCommand.SessionClassification classification, ImportContext ctx, CancellationToken ct) {
        var path = (string)classification.SourceMeta!["TranscriptPath"]!;
        if (!File.Exists(path)) return ImportOutcome.Failed;
        var cwd = classification.SourceMeta.TryGetValue("Cwd", out var c) ? c as string : null;
        var model = classification.SourceMeta.TryGetValue("Model", out var m) ? m as string : null;
        var start = StartPayload(classification.SessionId, cwd, model, classification.Meta.FirstTimestamp);
        if (!ctx.ForcePrivate && classification.Status == ImportCommand.ClassificationStatus.New && ctx.DefaultVisibility is not null) start["default_visibility"] = ctx.DefaultVisibility;
        if (!await PostAsync(ctx.HttpClient, ctx.BaseUrl, "session-start/kimi", start, ct)) return ImportOutcome.Failed;
        var startLine = classification.Status switch { ImportCommand.ClassificationStatus.Partial => classification.ResumeFromLine, ImportCommand.ClassificationStatus.AlreadyLoaded => classification.TotalLines, _ => 0 };
        int sent;
        try { sent = await SessionImporter.SendTranscriptBatches(ctx.HttpClient, ctx.BaseUrl, classification.SessionId, path, null, startLine, vendor: Vendor); }
        catch { return ImportOutcome.Failed; }
        if (!await PostAsync(ctx.HttpClient, ctx.BaseUrl, "session-end/kimi", EndPayload(classification.SessionId, cwd, classification.Meta.LastTimestamp), ct)) return ImportOutcome.Failed;
        return sent == 0 ? (startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped) : (startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded);
    }

    static IEnumerable<RootWire> RootWires(string root, bool kimiCode) {
        if (!Directory.Exists(root)) yield break;
        foreach (var wire in GuardedDiscovery.EnumerateFiles(root, "wire.jsonl")) {
            var parent = Path.GetDirectoryName(wire)!;
            if (kimiCode) {
                if (!string.Equals(Path.GetFileName(parent), "main", StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetFileName(Path.GetDirectoryName(parent)!), "agents", StringComparison.OrdinalIgnoreCase)) continue;
                var dir = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(parent)!)!);
                if (!dir.StartsWith("session_", StringComparison.OrdinalIgnoreCase)) continue;
                yield return new RootWire(wire, dir["session_".Length..], true);
            } else {
                if (!Guid.TryParse(Path.GetFileName(parent), out _) || !GroupId.IsMatch(Path.GetFileName(Path.GetDirectoryName(parent)!))) continue;
                yield return new RootWire(wire, Path.GetFileName(parent), false);
            }
        }
    }

    static readonly System.Text.RegularExpressions.Regex GroupId = new("^[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static IEnumerable<string> ChildWires(string rootWire, bool kimiCode) {
        var session = kimiCode ? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(rootWire)!)!)! : Path.GetDirectoryName(rootWire)!;
        var childRoot = kimiCode ? Path.Combine(session, "agents") : Path.Combine(session, "subagents");
        return Directory.Exists(childRoot)
            ? GuardedDiscovery.EnumerateFiles(childRoot, "wire.jsonl").Where(p => !string.Equals(p, rootWire, StringComparison.OrdinalIgnoreCase))
            : [];
    }

    static async Task<WireInfo> ReadWireInfoAsync(string path, CancellationToken ct) {
        string? cwd=null, model=null; DateTimeOffset? first=null, last=null;
        try {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { using var doc=JsonDocument.Parse(line); var r=doc.RootElement;
                    var timestamp = Timestamp(r, "time") ?? Timestamp(r, "created_at"); if (timestamp is { } ts) { first ??= ts; last=ts; }
                    if (r.GetPropertyOrNull("environmentDisclosure")?.GetPropertyOrNull("cwd") is { ValueKind: JsonValueKind.String } direct) cwd ??= direct.GetString();
                    if (r.GetPropertyOrNull("profile")?.GetPropertyOrNull("bind")?.GetPropertyOrNull("environmentDisclosure")?.GetPropertyOrNull("cwd") is { ValueKind: JsonValueKind.String } nested) cwd ??= nested.GetString();
                    if (r.GetPropertyOrNull("modelAlias") is { ValueKind: JsonValueKind.String } alias) model ??= alias.GetString();
                } catch (JsonException) { }
            }
        } catch { }
        return new WireInfo(cwd, model, first, last);
    }
    static DateTimeOffset? Timestamp(JsonElement root, string field) {
        if (!root.TryGetProperty(field, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var ms)) { try { return DateTimeOffset.FromUnixTimeMilliseconds(ms); } catch { return null; } }
        return value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts) ? ts : null;
    }
    static async Task<(int? Last, int Count)> ReadTranscriptStatsAsync(string path, CancellationToken ct) { int? last=null; var count=0; var i=0; await using var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite); using var r=new StreamReader(fs); while(await r.ReadLineAsync(ct) is { } line) { if(!string.IsNullOrWhiteSpace(line)){last=i;count++;} i++; } return(last,count); }
    static DateTimeOffset? FileTime(string p, bool creation) { try { return creation ? File.GetCreationTimeUtc(p) : File.GetLastWriteTimeUtc(p); } catch { return null; } }
    static string NormalizePath(string p) { try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar); } catch { return p.TrimEnd('/','\\'); } }
    static bool PathEquals(string a,string b) => string.Equals(a,b,OperatingSystem.IsWindows()||OperatingSystem.IsMacOS()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
    static ImportCommand.SessionClassification Make(DiscoveredSession s, SessionMetadata m, ImportCommand.ClassificationStatus status,int total,string? reason=null) => new(){SessionId=s.SessionId,FilePath="",EncodedCwd="",Meta=m,Status=status,Vendor="kimi",ProbeErrorReason=reason,TotalLines=total,SourceMeta=s.SourceMeta};
    static async Task<int?> FetchServerLastLineAsync(HttpClient http,string baseUrl,string id,CancellationToken ct) { using var resp=await http.GetWithRetryAsync($"{baseUrl}/api/sessions/{id}/last-line",ct:ct); if(resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)return null; if(!resp.IsSuccessStatusCode)throw new HttpRequestException(); using var doc=JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)); return doc.RootElement.TryGetProperty("last_line_number",out var n)&&n.ValueKind==JsonValueKind.Number?n.GetInt32():null; }
    static (string?,string?) ResolveExclusions(string? cwd,string? repo,ClassifyContext ctx) { string? er=null,ep=null; if(repo is not null&&ctx.ExcludedRepos?.Any(x=>string.Equals(x,repo,StringComparison.OrdinalIgnoreCase))==true)er=repo; if(cwd is not null&&ctx.ExcludedPaths is { } paths) foreach(var p in paths) if(PathExclusion.IsExcluded(cwd,[p])) {ep=PathExclusion.Normalize(p);break;} return(er,ep); }
    static JsonObject StartPayload(string id,string? cwd,string? model,DateTimeOffset? started) { var p=new JsonObject{{"hook_event_name","agentSpawn"},{"session_id",id}}; if(cwd is not null)p["cwd"]=cwd; if(cwd is not null&&GitRepository.FindRoot(cwd) is { } root)p["workspace_root"]=root; if(model is not null)p["model"]=model; if(started is { } t)p["started_at"]=t.ToString("O"); p["origin"]=ImportOrigins.Historical; return p; }
    static JsonObject EndPayload(string id,string? cwd,DateTimeOffset? ended) { var p=new JsonObject{{"hook_event_name","sessionEnd"},{"session_id",id},{"reason","historical-import"}};if(cwd is not null)p["cwd"]=cwd;if(ended is { } t)p["ended_at"]=t.ToString("O");p["origin"]=ImportOrigins.Historical;return p; }
    static async Task<bool> PostAsync(HttpClient c,string baseUrl,string route,JsonObject p,CancellationToken ct) { try { using var content=new StringContent(p.ToJsonString(),Encoding.UTF8,"application/json");using var response=await c.PostWithRetryAsync($"{baseUrl}/hooks/{route}",content,ct:ct);return response.IsSuccessStatusCode;}catch{return false;} }
    sealed record RootWire(string Path, string DashedId, bool KimiCode);
    sealed record WireInfo(string? Cwd,string? Model,DateTimeOffset? FirstTimestamp,DateTimeOffset? LastTimestamp);
}

file static class KimiJsonExtensions {
    public static JsonElement? GetPropertyOrNull(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : null;
}
