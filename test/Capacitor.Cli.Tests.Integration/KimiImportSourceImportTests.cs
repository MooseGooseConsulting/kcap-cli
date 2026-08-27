using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Kimi;
using WireMock.RequestBuilders;
using WireMock.Matchers;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

public class KimiImportSourceImportTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir _tmp = new();
    const string DashedSid = "11111111-2222-3333-4444-555555555555";

    public void Dispose() { _server.Stop(); _tmp.Dispose(); }

    string WriteSession(string? childAgentId = null) {
        var relative = $".kimi-code/sessions/wd_synthetic/session_{DashedSid}/agents/main/wire.jsonl";
        _tmp.CreateFile(relative, [
            """{"type":"metadata","created_at":1760000000000}""",
            """{"type":"profile.bind","modelAlias":"kimi-synthetic","environmentDisclosure":{"cwd":"/synthetic/work"},"time":1760000001000}""",
            """{"type":"turn.prompt","input":"invented prompt","time":1760000002000}""",
        ]);
        if (childAgentId is not null) {
            _tmp.CreateFile($".kimi-code/sessions/wd_synthetic/session_{DashedSid}/agents/{childAgentId}/wire.jsonl", [
                """{"type":"turn.prompt","input":"child prompt","time":1760000003000}""",
                """{"type":"turn.completed","time":1760000004000}""",
            ]);
        }
        return _tmp.Path;
    }

    string WriteLegacySession(string childAgentId) {
        var relative = $".kimi/sessions/0123456789abcdef0123456789abcdef/{DashedSid}/wire.jsonl";
        _tmp.CreateFile(relative, [
            """{"type":"metadata","created_at":1760000000000}""",
            """{"type":"profile.bind","modelAlias":"kimi-synthetic","environmentDisclosure":{"cwd":"/synthetic/work"},"time":1760000001000}""",
            """{"type":"turn.prompt","input":"invented prompt","time":1760000002000}""",
        ]);
        _tmp.CreateFile($".kimi/sessions/0123456789abcdef0123456789abcdef/{DashedSid}/subagents/{childAgentId}/wire.jsonl", [
            """{"type":"turn.prompt","input":"legacy child","time":1760000003000}""",
            """{"type":"turn.completed","time":1760000004000}""",
        ]);
        return _tmp.Path;
    }

    void StubLifecycle() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/kimi").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end/kimi").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
    }

    [Test]
    public async Task ImportSession_posts_kimi_lifecycle_before_and_after_raw_wire_batch() {
        var home = WriteSession();
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        StubLifecycle();
        using var client = new HttpClient();
        var source = new KimiImportSource(home, _ => Task.FromResult<RepositoryPayload?>(null));
        var classified = await source.ClassifyAsync(await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None),
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await Assert.That(await source.ImportSessionAsync(classified[0], new ImportContext(client, _server.Url!, false), CancellationToken.None)).IsEqualTo(ImportOutcome.Loaded);
        var posts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST").Select(e => e.RequestMessage.Path).ToArray();
        await Assert.That(posts).IsEquivalentTo(["/hooks/session-start/kimi", "/hooks/transcript", "/hooks/session-end/kimi"]);
        await Assert.That(_server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/transcript").RequestMessage.Body!).Contains("\"vendor\":\"kimi\"");
    }

    [Test]
    [Arguments(0)]
    [Arguments(2)]
    public async Task ImportSession_uses_existing_root_watermark(int watermark) {
        var home = WriteSession();
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""{"last_line_number":{{watermark}}}"""));
        StubLifecycle();
        using var client = new HttpClient();
        var source = new KimiImportSource(home, _ => Task.FromResult<RepositoryPayload?>(null));
        var classified = await source.ClassifyAsync(await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None),
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);
        var expected = watermark == 0 ? ImportCommand.ClassificationStatus.Partial : ImportCommand.ClassificationStatus.AlreadyLoaded;
        await Assert.That(classified[0].Status).IsEqualTo(expected);
        await Assert.That(await source.ImportSessionAsync(classified[0], new ImportContext(client, _server.Url!, false), CancellationToken.None)).IsEqualTo(ImportOutcome.Resumed);
    }

    [Test]
    public async Task ImportSession_imports_kimi_code_child_with_its_own_agent_stream_and_lifecycle() {
        var home = WriteSession(childAgentId: "agent-7");
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        StubLifecycle();
        foreach (var route in new[] { "/hooks/subagent-start", "/hooks/subagent-stop" })
            _server.Given(Request.Create().WithPath(route).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();
        var source = new KimiImportSource(home, _ => Task.FromResult<RepositoryPayload?>(null));
        var classified = await source.ClassifyAsync(await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None),
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);
        var result = await source.ImportSessionAsync(classified[0], new ImportContext(client, _server.Url!, false), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(result.SentChildContent).IsTrue();
        var posts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST").Select(e => e.RequestMessage.Path).ToList();
        await Assert.That(posts).IsEquivalentTo(["/hooks/session-start/kimi", "/hooks/transcript", "/hooks/subagent-start", "/hooks/transcript", "/hooks/subagent-stop", "/hooks/session-end/kimi"]);
        await Assert.That(posts.IndexOf("/hooks/session-start/kimi")).IsLessThan(posts.IndexOf("/hooks/transcript"));
        await Assert.That(posts.IndexOf("/hooks/transcript")).IsLessThan(posts.IndexOf("/hooks/subagent-start"));
        await Assert.That(posts.IndexOf("/hooks/subagent-start")).IsLessThan(posts.IndexOf("/hooks/subagent-stop"));
        await Assert.That(posts.LastIndexOf("/hooks/subagent-stop")).IsLessThan(posts.IndexOf("/hooks/session-end/kimi"));
        var childStart = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/subagent-start").RequestMessage.Body!;
        await Assert.That(childStart).Contains("\"agent_id\":\"agent-7\"");
        var childBatch = _server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => e.RequestMessage.Body!).Single(body => body.Contains("\"agent_id\":\"agent-7\""));
        await Assert.That(childBatch).Contains("\"vendor\":\"kimi\"");
    }

    [Test]
    public async Task ImportSession_imports_legacy_kimi_subagent_layout() {
        var home = WriteLegacySession("worker-alpha");
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        StubLifecycle();
        foreach (var route in new[] { "/hooks/subagent-start", "/hooks/subagent-stop" })
            _server.Given(Request.Create().WithPath(route).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();
        var source = new KimiImportSource(home, _ => Task.FromResult<RepositoryPayload?>(null));
        var classified = await source.ClassifyAsync(await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None),
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);
        var result = await source.ImportSessionAsync(classified[0], new ImportContext(client, _server.Url!, false), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(result.SentChildContent).IsTrue();
        var childStart = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/subagent-start").RequestMessage.Body!;
        await Assert.That(childStart).Contains("\"agent_id\":\"worker-alpha\"");
    }

    [Test]
    public async Task ImportSession_replay_repairs_complete_child_lifecycle_without_resending_its_content() {
        var home = WriteSession(childAgentId: "agent-3");
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").WithParam("agentId", "agent-3").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        StubLifecycle();
        foreach (var route in new[] { "/hooks/subagent-start", "/hooks/subagent-stop" })
            _server.Given(Request.Create().WithPath(route).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();
        var source = new KimiImportSource(home, _ => Task.FromResult<RepositoryPayload?>(null));
        var classified = await source.ClassifyAsync(await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None),
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
        var result = await source.ImportSessionAsync(classified[0], new ImportContext(client, _server.Url!, false), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);
        await Assert.That(result.SentChildContent).IsFalse();
        var childPosts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST").Select(e => e.RequestMessage.Path).ToList();
        await Assert.That(childPosts.Count(p => p == "/hooks/transcript")).IsEqualTo(0);
        var repairStart = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/subagent-start").RequestMessage.Body!;
        await Assert.That(repairStart).Contains("\"strict\":true");
    }

    [Test]
    public async Task ImportSession_retries_a_rejected_child_batch_without_claiming_child_content_was_sent() {
        var home = WriteSession(childAgentId: "agent-9");
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        StubLifecycle();
        _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/subagent-stop").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        // Root batches have no agent_id; only the child is rejected. The strict sender catches
        // this response, leaves stop unsent, and makes the next import eligible to retry it.
        _server.Given(Request.Create().WithPath("/hooks/transcript").WithBody(new WildcardMatcher("""*"agent_id":"agent-9"*""")).UsingPost())
            .AtPriority(1).RespondWith(Response.Create().WithStatusCode(500));

        using var client = new HttpClient();
        var source = new KimiImportSource(home, _ => Task.FromResult<RepositoryPayload?>(null));
        var classified = await source.ClassifyAsync(await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None),
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);
        var result = await source.ImportSessionAsync(classified[0], new ImportContext(client, _server.Url!, false), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(result.SentChildContent).IsFalse();
        var posts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST").Select(e => e.RequestMessage.Path).ToList();
        await Assert.That(posts.Contains("/hooks/subagent-start")).IsTrue();
        await Assert.That(posts.Contains("/hooks/subagent-stop")).IsFalse();
        await Assert.That(posts.Contains("/hooks/session-end/kimi")).IsTrue();

        // A re-run starts the child again and sends the previously rejected content. The root
        // classification is intentionally reused: this exercises the child retry independently
        // of the root's watermark classification.
        _server.Reset();
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        StubLifecycle();
        foreach (var route in new[] { "/hooks/subagent-start", "/hooks/subagent-stop" })
            _server.Given(Request.Create().WithPath(route).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        var retry = await source.ImportSessionAsync(classified[0], new ImportContext(client, _server.Url!, false), CancellationToken.None);
        await Assert.That(retry.SentChildContent).IsTrue();
        var retryPosts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST").Select(e => e.RequestMessage.Path).ToList();
        await Assert.That(retryPosts.Contains("/hooks/subagent-start")).IsTrue();
        await Assert.That(retryPosts.Contains("/hooks/subagent-stop")).IsTrue();
    }

    [Test]
    public async Task ImportSession_resumes_a_partial_child_from_its_own_watermark() {
        var home = WriteSession(childAgentId: "agent-4");
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").WithParam("agentId", "agent-4").UsingGet())
            .AtPriority(1).RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":0}"""));
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubLifecycle();
        foreach (var route in new[] { "/hooks/subagent-start", "/hooks/subagent-stop" })
            _server.Given(Request.Create().WithPath(route).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();
        var source = new KimiImportSource(home, _ => Task.FromResult<RepositoryPayload?>(null));
        var classified = await source.ClassifyAsync(await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None),
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);
        var result = await source.ImportSessionAsync(classified[0], new ImportContext(client, _server.Url!, false), CancellationToken.None);

        await Assert.That(result.SentChildContent).IsTrue();
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Url!.Contains("agentId=agent-4", StringComparison.Ordinal))).IsTrue();
        var childBatch = _server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => e.RequestMessage.Body!).Single(body => body.Contains("\"agent_id\":\"agent-4\""));
        await Assert.That(childBatch).Contains("\"line_numbers\":[1]");
    }

    [Test]
    public async Task ImportSession_does_not_finalize_when_a_strict_root_batch_is_rejected() {
        var home = WriteSession();
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        StubLifecycle();
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .AtPriority(1).RespondWith(Response.Create().WithStatusCode(500));

        using var client = new HttpClient();
        var source = new KimiImportSource(home, _ => Task.FromResult<RepositoryPayload?>(null));
        var classified = await source.ClassifyAsync(await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None),
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);
        var result = await source.ImportSessionAsync(classified[0], new ImportContext(client, _server.Url!, false), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Failed);
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == "/hooks/session-end/kimi")).IsFalse();
    }
}
