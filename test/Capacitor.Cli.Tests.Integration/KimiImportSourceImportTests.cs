using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Kimi;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

public class KimiImportSourceImportTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir _tmp = new();
    const string DashedSid = "11111111-2222-3333-4444-555555555555";

    public void Dispose() { _server.Stop(); _tmp.Dispose(); }

    string WriteSession() {
        var relative = $".kimi-code/sessions/wd_synthetic/session_{DashedSid}/agents/main/wire.jsonl";
        _tmp.CreateFile(relative, [
            """{"type":"metadata","created_at":1760000000000}""",
            """{"type":"profile.bind","modelAlias":"kimi-synthetic","environmentDisclosure":{"cwd":"/synthetic/work"},"time":1760000001000}""",
            """{"type":"turn.prompt","input":"invented prompt","time":1760000002000}""",
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
}
