using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Kimi;

namespace Capacitor.Cli.Tests.Unit.Harness.Kimi;

public class KimiImportSourceTests {
    const string Sid1 = "11111111-2222-3333-4444-555555555555";
    const string Sid2 = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    static void WriteRoot(TempDir tmp, string relative, string cwd = "/invented/work", string model = "kimi-synthetic") =>
        tmp.CreateFile(relative, [
            """{"type":"metadata","created_at":1760000000000}""",
            $$"""{"type":"profile.bind","modelAlias":"{{model}}","environmentDisclosure":{"cwd":"{{cwd}}"},"time":1760000001000}""",
            """{"type":"turn.prompt","input":"invented prompt","time":1760000002000}""",
        ]);

    [Test]
    public async Task discovery_finds_kimi_code_main_wire_and_child_metadata() {
        using var tmp = new TempDir();
        WriteRoot(tmp, $".kimi-code/sessions/wd_invented_x/session_{Sid1}/agents/main/wire.jsonl");
        tmp.CreateFile($".kimi-code/sessions/wd_invented_x/session_{Sid1}/agents/agent-1/wire.jsonl", """{"type":"turn.prompt","input":"child"}""");
        tmp.CreateFile($".kimi-code/sessions/wd_invented_x/session_{Sid1}/agents/agent-2/wire.jsonl", "");
        var found = await new KimiImportSource(tmp.Path).DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));
        await Assert.That(found[0].Vendor).IsEqualTo("kimi");
        await Assert.That(found[0].Cwd).IsEqualTo("/invented/work");
        await Assert.That(found[0].SourceMeta!["Model"]).IsEqualTo("kimi-synthetic");
        await Assert.That(((string[])found[0].SourceMeta["ChildTranscriptPaths"]!).Length).IsEqualTo(2);
    }

    [Test]
    public async Task discovery_finds_legacy_kimi_root_but_not_subagents() {
        using var tmp = new TempDir();
        WriteRoot(tmp, $".kimi/sessions/0123456789abcdef0123456789abcdef/{Sid1}/wire.jsonl");
        tmp.CreateFile($".kimi/sessions/0123456789abcdef0123456789abcdef/{Sid1}/subagents/agent-x/wire.jsonl", """{"type":"turn.prompt","input":"child"}""");
        var found = await new KimiImportSource(tmp.Path).DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));
        await Assert.That(((string[])found[0].SourceMeta!["ChildTranscriptPaths"]!).Length).IsEqualTo(1);
    }

    [Test]
    public async Task discovery_deduplicates_root_ids_and_applies_filters() {
        using var tmp = new TempDir();
        WriteRoot(tmp, $".kimi-code/sessions/wd_invented_x/session_{Sid1}/agents/main/wire.jsonl", "/invented/a");
        WriteRoot(tmp, $".kimi/sessions/0123456789abcdef0123456789abcdef/{Sid1}/wire.jsonl", "/invented/b");
        WriteRoot(tmp, $".kimi/sessions/0123456789abcdef0123456789abcdef/{Sid2}/wire.jsonl", "/invented/b");
        var source = new KimiImportSource(tmp.Path);
        var found = await source.DiscoverAsync(new DiscoveryFilters("/invented/b", Sid2, null, 0), CancellationToken.None);
        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].SessionId).IsEqualTo(Sid2.Replace("-", ""));
    }

    [Test]
    public async Task discovery_skips_non_main_and_tolerates_malformed_lines() {
        using var tmp = new TempDir();
        tmp.CreateFile($".kimi-code/sessions/wd_x/session_{Sid1}/agents/agent-1/wire.jsonl", """{"type":"turn.prompt"}""");
        tmp.CreateFile($".kimi-code/sessions/wd_x/session_{Sid2}/agents/main/wire.jsonl", ["not-json", """{"type":"turn.prompt","time":1760000002000}""", """{"type":"context.append_message""" ]);
        var found = await new KimiImportSource(tmp.Path).DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].SessionId).IsEqualTo(Sid2.Replace("-", ""));
    }

    [Test]
    public async Task availability_and_capabilities_follow_historical_only_contract() {
        using var tmp = new TempDir();
        var absent = new KimiImportSource(tmp.Path);
        await Assert.That(absent.IsAvailable).IsFalse();
        await Assert.That(absent.SupportsTitleGeneration).IsFalse();
        await Assert.That(absent.AttachesChildContentOnReplay).IsFalse();
        WriteRoot(tmp, $".kimi/sessions/0123456789abcdef0123456789abcdef/{Sid1}/wire.jsonl");
        await Assert.That(new KimiImportSource(tmp.Path).IsAvailable).IsTrue();
    }
}
