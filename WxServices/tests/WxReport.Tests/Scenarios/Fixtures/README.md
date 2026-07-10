# KDWH 2026-04-21 replay fixtures

Recorded Anthropic responses for the KDWH double-send scenario replay
(`KdwhScenarioReplayTests`), captured from the live Claude API by
`KdwhScenarioReplayRecorder`.

- `kdwh-853-skip.recorded.json` — the 8:53 cycle; Claude calls `skip_send`
  (observed light rain matches the committed rainy forecast → not news). This is
  the 2026-04-21 double-send, fixed.
- `kdwh-853-storm-send.recorded.json` — the off-forecast severe-storm mutation
  (positive control); Claude calls `submit_reconciled_report`.
- `kdwh-853-skip.trace.txt` — the skip `reasoning_trace` from that capture; also
  WX-81 AC#4 evidence (tier-aware language in a real reconciliation trace).

These are **genuine captures**, not hand-written stubs — the whole point of the
recorded approach is that the replay verifies Claude's *actual* judgment, not a
canned answer.

## Re-recording

Re-run the recorder after any material change to the reconciliation prompt
(`ReconcilerPrompts`) or to the `Kdwh20260421Fixture` inputs, then commit the
overwritten `*.recorded.json` and `kdwh-853-skip.trace.txt`.

**Inject the two variables via a RunSettings file — NOT inline env vars.** On this
machine `dotnet` is the Windows `dotnet.exe` (invoked from WSL), and under .NET SDK 9
the `dotnet test` **test host does not inherit the shell environment** — so an inline
`WX_RECORD_KDWH=1 … dotnet test` *or* an `export`ed variable is silently ignored: the
recorder's `WX_RECORD_KDWH` gate returns early and you get a fast, green, **zero-diff**
run that records nothing (the "silently stale fixtures" trap, inverted). VSTest reads
env vars from a `.runsettings` `<EnvironmentVariables>` block and sets them in the host,
so that is the reliable channel.

Put the settings file at a **Windows-accessible** path — a WSL `/tmp/…` argument is
mangled to `C:\tmp\…` by the interop layer and `dotnet.exe` can't find it — pass an
explicit Windows path to `--settings`, and `rm` it after: it holds the key, so keep it
out of the repo and shell history. From the `WxServices` directory, with
`ANTHROPIC_API_KEY` already exported in the shell:

```bash
RS=/mnt/c/Code/Temp/kdwh-record.runsettings
trap 'rm -f "$RS"' EXIT INT TERM          # delete the key file on ANY exit — even a failed/interrupted run
cat > "$RS" <<XML
<?xml version="1.0" encoding="utf-8"?>
<RunSettings><RunConfiguration><EnvironmentVariables>
  <WX_RECORD_KDWH>1</WX_RECORD_KDWH>
  <ANTHROPIC_API_KEY>${ANTHROPIC_API_KEY}</ANTHROPIC_API_KEY>
</EnvironmentVariables></RunConfiguration></RunSettings>
XML
dotnet test WxServices.CI.slnf --filter FullyQualifiedName~KdwhScenarioReplayRecorder --settings 'C:\Code\Temp\kdwh-record.runsettings'
rm -f "$RS"; trap - EXIT INT TERM
```

(The `trap` guarantees the key-bearing settings file is removed even if `dotnet test`
fails or you Ctrl-C it. The unquoted `<<XML` heredoc expands `${ANTHROPIC_API_KEY}` into
the file; the single-quoted `'C:\Code\Temp\…'` keeps the backslashes for `dotnet.exe`.)

**A real recording takes ~40 s+ (two live Claude calls) and leaves the three fixtures
modified in `git status`.** A fast (< 10 s), zero-diff run means the env never reached
the host — re-check the RunSettings path. The recorder is opt-in: a no-op unless
`WX_RECORD_KDWH=1` reaches the host. If it is set but `ANTHROPIC_API_KEY` is missing, the
recorder fails loudly rather than silently committing stale fixtures. If real Claude
stops skipping the negative case, that is a genuine finding about the tier prompt —
investigate before trusting it.
