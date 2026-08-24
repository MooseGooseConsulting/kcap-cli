# Kimi Code historical import and live-capture plan

**Goal:** Add Kimi Code as a first-class kcap source: safely discover and import
historical Kimi transcripts, then optionally add live capture. Keep all Kimi
parsing, discovery, retries, and test coverage in the open-source CLI. Treat
the closed server's accepted vendor/normalizer contract as an explicit release
gate rather than an assumption.

**Scope:** This plan covers the `MooseGooseConsulting/kcap-cli` fork of
`kurrent-io/kcap-cli`. It deliberately does not
check in, upload, or reproduce a real user's Kimi transcript in fixtures.

**Architecture:** A `KimiImportSource : IImportSource` uses Kiro/Pi's local
discovery, root-watermark, and root lifecycle mechanics. Its child-stream
routing follows Gemini/Antigravity: discover the sibling child wires, register
each with the existing subagent lifecycle routes, and send it against the root
session with a stable `agent_id` and its own watermark. This translates existing
CLI behavior; it does not invent a Kimi-specific server protocol.
A future `kcap hook --kimi`
dispatcher and Kimi plugin/adapter would start the same watcher for active
sessions. Historical import does not need hooks.

---

## Observed Kimi local layout and wire contract

Two Kimi layouts were observed locally on Windows. Both are supported
discovery variants; neither replaces the other. Their platform/version
selection boundary has not been established, so discovery checks both roots.

```text
~/.kimi-code/sessions/
  wd_<workspace-slug>_<suffix>/
    session_<dashed-uuid>/
      agents/
        main/wire.jsonl
        agent-N/wire.jsonl

~/.kimi/sessions/
  <32-lowercase-hex-directory>/
    <dashed-uuid>/
      wire.jsonl
      subagents/
        <agent-id>/wire.jsonl
```

In the `.kimi-code` layout, `agents/main/wire.jsonl` is the root stream and
`agents/agent-N/wire.jsonl` files are children; the `session_` directory's
dashed UUID is the session identity. In the `.kimi` layout, `wire.jsonl`
directly below the dashed UUID is the root stream and
`subagents/<agent-id>/wire.jsonl` files are children. Its leading 32-character
directory is an observed grouping level, not the canonical session id. The
wire records use Unix epoch **milliseconds** in `time`.

Observed top-level records (field values omitted intentionally):

| Record | Relevant fields | Import use |
| --- | --- | --- |
| `metadata` | `protocol_version`, `created_at` | format/version and fallback start time |
| `profile.bind` | `modelAlias`, `environmentDisclosure.cwd`, `time` | cwd, model, start metadata |
| `turn.prompt` / `turn.steer` | `input`, `origin`, `time` | user input / steering boundary |
| `context.append_message` | `message.role`, `message.content`, `message.toolCalls`, `time` | user/assistant messages |
| `context.append_loop_event` | `event.type`, `uuid`, `turnId`, `step`, `time` | incremental content plus `tool.call` and `tool.result` |
| `turn.ended` | `turnId`, `reason`, `durationMs`, `time` | turn completion and fallback end time |
| `task.started` / `task.terminated` | `info`, `outputTail`, `time` | command-task context |
| `plugin.session_start` | `time` | evidence that Kimi records session startup; not proof of a callable plugin API |

Observed nested loop-event kinds are `step.begin`, `content.part`, `tool.call`,
`tool.result`, and `step.end`. The importer must preserve original lines and
their order; the server normalizer—not the discovery pass—owns their semantic
interpretation.

### Privacy and fixture rules

- Never add a real `~/.kimi-code` or `~/.kimi` file to the kcap repository or
  test output.
- Build small synthetic fixtures with only invented prompts, paths, command
  names, UUIDs, and timestamps.
- Test both layout variants plus structural variants: no `profile.bind`,
  malformed line, no `.kimi-code` `main` root, empty child, incomplete final
  line, and a session with multiple children.
- Do not log raw transcript content in normal CLI diagnostics. Report paths,
  session IDs, line counts, and parser error categories only.

---

## Historical-import evidence boundary

This plan records the CLI baseline only. It does not define a Kimi server
normalizer, a Kimi-specific HTTP route, or a new server probe. The implementation
uses the existing root routed-import surfaces from Kiro/Pi and the existing
subagent surfaces from Gemini/Antigravity. Server acceptance remains a
server-owned release boundary; local WireMock tests verify the CLI request
contract without uploading a real transcript.

---

## Phase 1 — Kimi historical importer (open-source CLI)

### Root baseline and child translation

Implement one routed `IImportSource`, with a test-only sessions-root override.
Copy Kiro/Pi for the root stream and translate Gemini/Antigravity for child
streams:

1. `Vendor` is `"kimi"`; `IsAvailable` is true when either observed sessions
   root exists;
   `SupportsTitleGeneration` is `false` and `AttachesChildContentOnReplay` is
   `true`, because an already-loaded root can attach a child. Classifications set `FilePath = ""` and `EncodedCwd = ""` so the
   routed `ImportSessionAsync` path owns the work.
2. Discover both observed roots recursively: admit
   `~/.kimi-code/sessions/**/session_<dashed-uuid>/agents/main/wire.jsonl` and
   `~/.kimi/sessions/**/<dashed-uuid>/wire.jsonl` only. Normalize each
   directory UUID to the existing dashless session-id form and de-duplicate it
   with a local Kimi seen set, matching Pi's pattern, if both layouts contain the same session. Store root
   wire path, child paths, cwd, model, and timestamps in `SourceMeta`; apply
   `--session`, `--cwd`, and `--since` before classification. Read only locally;
   use synthetic fixtures.
3. Classify with Kiro/Pi's `ReadTranscriptStatsAsync`: count nonblank records,
   retain the last nonblank physical index, use the existing root
   `GET /api/sessions/{id}/last-line` helper, and classify `New`, `Partial`, or
   `AlreadyLoaded` from that root watermark. A 404/204 means no watermark; any
   other non-success is `ProbeError`. Preserve Kiro/Pi's repository/path
   exclusion resolution and probe-error/too-short outcomes.
4. The import-relevant predicate is not inferred by discovery. Kiro's predicate
   is `kind` in `Prompt`, `AssistantMessage`, `ToolResults`; Pi's is its
   normalizer's documented emit set. Kimi has no equivalent evidence in this
   checkout, so no Kimi-only skip table or custom sender is specified here.
   The copy baseline sends every nonblank root wire line and retains physical
   line numbers.
5. Import in the Kiro/Pi order: `POST /hooks/session-start/kimi`, call
   `SessionImporter.SendTranscriptBatches` for the root with `agentId: null`,
   `startLine` of `0`, `ResumeFromLine`, or `TotalLines` for New/Partial/
   AlreadyLoaded, then `POST /hooks/session-end/kimi`. As in Kiro/Pi,
   `TotalLines` is the nonblank count even though `startLine` is interpreted in
   the file's physical-index space; retain that existing behavior rather than
   silently introducing a Kimi-specific cursor scheme. Start/end payloads use
   the existing helpers' fields: `hook_event_name`, `session_id`, optional
   `cwd`, optional `workspace_root`, optional ISO-8601 start/end timestamps,
   `origin: historical-import`, and an import reason. New sessions add
   `default_visibility` only when not force-private; a source with no existing
   force-private behavior omits the field under `--private`, as Kiro does.
6. Use strict sender semantics: `SendTranscriptBatches` reads with
   `FileShare.ReadWrite`, skips blanks, posts up to 100 raw lines per batch with
   their physical indexes and `vendor=kimi`, with `failOnError: true`. A rejected
   root batch returns `Failed` before session-end, leaving the run retryable.
   The result is `Loaded`/`Resumed` when lines were sent, otherwise
   `Skipped`/`Resumed` according to `startLine`.
7. Do not synthesize a title. Kiro's `POST /hooks/set-title` is justified by its
   sibling metadata title; Pi uses the server fallback. No corresponding Kimi
   title source has been established here, so use the Pi behavior.

### Unavoidable local Kimi deltas

- Root discovery has two local shapes: `.kimi-code` uses
  `session_<dashed-uuid>/agents/main/wire.jsonl`; `.kimi` uses
  `<dashed-uuid>/wire.jsonl` below a leading 32-hex grouping directory. The
  dashed UUID supplies the session identity in both; the `.kimi` grouping
  directory does not.
- Children are `.kimi-code` `agents/agent-N/wire.jsonl` and `.kimi`
  `subagents/<agent-id>/wire.jsonl`. Their immediate parent directory name is
  the stable server-facing `agent_id`. For every nonempty child, use
  `GET /api/sessions/{root}/last-line?agentId={agentId}`; post `subagent-start`,
  send strict transcript batches to the root with that `agentId`, then post
  `subagent-stop`, all before root `session-end`. A complete child gets strict
  start/stop lifecycle repair without a content resend. Child failure is
  nonfatal to already-accepted root content and is retried on re-import.
- Parse `profile.bind.environmentDisclosure.cwd`, `modelAlias`, metadata
  `created_at`, and millisecond `time` only as local metadata when present;
  use the same filesystem timestamp fallbacks as Kiro/Pi. Do not let absent or
  malformed metadata prevent a readable root wire from being discovered.

### Tests to copy

- Copy `KiroImportSourceTests` for UUID discovery, sibling/local metadata, and
  the import-relevance table; copy `PiImportSourceTests.WriteSession` and its
  recursive discovery, invalid-file, UUID fallback, filter, availability, and
  title-capability coverage for the Kimi root tree.
- Copy `PiImportSourceImportTests`' `WireMockServer`, `TempDir`,
  `WriteSessionFile`, and New/Partial/AlreadyLoaded routed lifecycle tests.
  Add Gemini/Antigravity-style coverage for both Kimi child layouts: parent /
  child lifecycle ordering, directory-to-agent-id mapping, child watermark
  resume and complete-child lifecycle repair, rejected child batch retry, and
  `SentChildContent` only after an accepted child batch.
- Add Kimi to `ImportVisibilityTests` through `RoutedSourceCase`,
  `StubAllHookEndpoints`, `RoutedClassification`, and `SessionStartBody` with
  `OwnPrivateStamp: false`; copy Kiro's three visibility cases.
- Add Kimi to the true set in `ReplayChildContentCapabilityTests` and to its
  exhaustive source list. Add a `RoutedReplayPrivatizeTests`-style end-to-end
  routed-loop case proving an AlreadyLoaded root with a new child receives a
  `{"visibility":"none"}` PUT under `--private` and none without it.

### Registration boundary

Register the source only in historical-import selection (`Program.cs` and the
import-vendor selection/help path). Keep `KnownImportVendorFlags` separate from
`KnownHarnessVendorFlags`; the latter remains the exact `HarnessCatalog` set.
Do not add Kimi to `HarnessCatalog`, setup nudges, plugin installation, or
`kcap hook --kimi`; those are live-integration surfaces and remain a separate phase.

---

## Phase 2 — live Kimi capture (separate PR after historical import ships)

Historical import requires no hooks. Live capture needs an adapter only if Kimi
exposes a stable callback/plugin configuration surface. The observed
`plugin.session_start` wire record is evidence of an event, not proof that a
shell hook can be registered.

### Separate live-integration discovery

- [ ] Consult Kimi's official plugin/hook documentation and inspect the locally
  installed Kimi configuration format. Record the exact supported callbacks,
  payload shape, plugin install location, and Windows/macOS/Linux differences.
- [ ] If no supported lifecycle callback exists, implement no unsupported
  background polling. Keep historical import as the supported integration.

### Required live-capture behavior

If Kimi has a supported integration surface, add a `kcap hook --kimi` command,
a parser/installer under `Harness/Kimi`, and a `plugin install --kimi` path.

1. **Start:** on Kimi session start, identify `{sessionId, agentId, cwd,
   wireFile, startedAt, model}`; post one idempotent start lifecycle event and
   launch/tell the daemon to tail the specified wire file.
2. **Streaming:** tail exact appended physical lines, including content/tool
   loop events, preserving the same line numbers used by historical import.
   Use the existing durable hook/transcript spool so temporary auth/network
   failures retry rather than dropping events. Buffer an incomplete final JSONL
   record without posting it or advancing the durable cursor; when Kimi appends
   the remaining bytes, reassemble and emit that same physical line exactly
   once.
3. **Children:** if a supported integration is established, register the
   observed `.kimi-code` `agents/agent-N/wire.jsonl` and `.kimi`
   `subagents/<agent-id>/wire.jsonl` paths as child streams under the root Kimi
   session. A child must not end the parent.
4. **End:** use Kimi's documented end callback when available. Otherwise only
   synthesize end after a documented, conservative process-exit/idle rule; do
   not use an arbitrary timer that can close a long-running task.
5. **Safety:** hook invocation must be bounded, idempotent, silent on success,
   avoid leaking transcripts to stdout/stderr, and leave Kimi usable when kcap
   is offline or unauthenticated.

### Live-capture tests

- [ ] Installer/parser tests: install, idempotent reinstall, removal, malformed
  user config preservation, and platform path quoting.
- [ ] Hook tests: start deduplication, start-before-first-line race, child
  routing, reconnect/retry, end sequencing, expired token, and disabled repo.
- [ ] Append half of a synthetic JSONL record, poll/retry, then append its
  remainder plus another record. Prove the watcher posts no malformed partial
  payload, advances no cursor over the partial record, emits each physical
  index once after completion, and retains those guarantees across an
  intervening network failure.
- [ ] End-to-end local test: append synthetic lines while watcher runs and
  compare streamed output with historical-import output for the same fixture,
  including the split-final-line case.
- [ ] Authorized Kimi smoke: one root session plus a child, restart kcap during
  the session, then verify no loss or duplication server-side.

---

## Fork review workflow

`kurrent-io/kcap-cli` currently grants this account **READ** permission, so
review happens in the user's organization fork. Keep both remotes explicit and
open the review PR inside that fork; do not open an upstream PR without later
explicit authorization:

```powershell
# From the local kcap-cli checkout; preserves origin as the upstream remote.
gh repo fork kurrent-io/kcap-cli --org MooseGooseConsulting --remote --remote-name fork
git push --set-upstream fork feat/kimi-history-import
gh pr create --repo MooseGooseConsulting/kcap-cli `
  --base main --head feat/kimi-history-import `
  --title "feat: import Kimi Code history"
```

Before opening the PR:

- [ ] Rebase/merge the current upstream `main` and run the focused tests,
  complete unit suite required by the repository, build, and NativeAOT publish.
- [ ] Include fixture provenance (synthetic), OS/runtime used, and the
  documented evidence supporting any server-side Kimi behavior.
- [ ] State clearly whether the PR is importer-only or includes a certified
  live Kimi integration. Do not open it as a draft once the checks pass.
- [ ] Keep live hooks in a follow-up PR unless the historical importer and
  server normalizer have already been validated together.

## Commit sequence

1. `docs: plan Kimi Code history import` (this plan).
2. `feat: discover Kimi Code session transcripts` (paths, reader, unit tests).
3. `feat: import Kimi Code session history` (source, registration, HTTP tests,
   help text).
4. `feat: capture live Kimi Code sessions` (only after Phase 2 validation).

## Documentation lifecycle

This is an unexecuted implementation plan and remains in
`docs/superpowers/plans/` for review. Before implementation begins, promote
the approved architecture and closed-server contract to a dated design in
`docs/superpowers/specs/`. Once the work is executed, record the delivered
behavior and validation evidence in `docs/CHANGES.md`, then remove this
execution plan rather than letting it become stale documentation.
