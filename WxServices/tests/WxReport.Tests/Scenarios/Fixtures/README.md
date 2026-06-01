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

PowerShell (from the `WxServices` directory):

```powershell
$env:WX_RECORD_KDWH = "1"
$env:ANTHROPIC_API_KEY = "sk-ant-..."
dotnet test WxServices.CI.slnf --filter FullyQualifiedName~KdwhScenarioReplayRecorder
Remove-Item Env:WX_RECORD_KDWH, Env:ANTHROPIC_API_KEY
```

WSL bash equivalent:

```bash
WX_RECORD_KDWH=1 ANTHROPIC_API_KEY=sk-ant-... \
  dotnet test WxServices.CI.slnf --filter FullyQualifiedName~KdwhScenarioReplayRecorder
```

The recorder is opt-in: a no-op unless `WX_RECORD_KDWH=1`. If it is set but
`ANTHROPIC_API_KEY` is missing, the recorder fails loudly rather than silently
committing stale fixtures. If real Claude stops skipping the negative case, that
is a genuine finding about the tier prompt — investigate before trusting it.
