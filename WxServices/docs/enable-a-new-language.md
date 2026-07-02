# Enable a new report language

This runbook covers the full lifecycle of turning on a new recipient-report language, from
enabling it in WxManager through the **enable-time translation-QA audit** that gives us
independent confidence the generated report actually reads correctly before any recipient is
assigned to it.

It is the operator/developer companion to the architecture in
[`DESIGN.md`](../DESIGN.md) §4.2 (Generation-on-enable, Enable-time translation QA) and §4.7
(the WxManager Translation-QA tab). The per-step *test procedures* it points to remain the
authoritative, executable detail; this document is the connective tissue — the order to do
things in and why.

> **Scope note.** Enable-time QA is an **operator-run audit that produces an artifact for human
> judgment — never an automated gate**. A language becomes usable when a human has read the audit
> and is satisfied, not when a script says so.

---

## The lifecycle at a glance

| # | Step | Where | Result |
|---|------|-------|--------|
| 1 | Enable the language | WxManager → **Languages** tab | language marked **PENDING** |
| 2 | Generation-on-enable (WX-172) | WxReport.Svc, automatic on next cycle | **READY** / BLOCKED / FAILED |
| 3 | Run the enable-time translation-QA harness | `WxReport.Tools.TranslationQa` console tool | a `{iso}.{stamp}` judge package |
| 4 | Read the audit and adjudicate | WxManager → **Translation-QA** tab | vetted vocabulary (Copy→DB) |
| 5 | Assign recipients | WxManager → **Recipients** tab | recipients receive the language |

Steps 1–2 stand up the templates; steps 3–4 are the QA gate we apply before trusting them;
step 5 is the payoff. Steps 3–4 can be repeated any time (see **Re-running** below).

---

## 1–2. Enable the language and let it generate

1. In **WxManager → Languages**, enable the target language. Enabling marks it **PENDING** — it
   does **not** require pre-existing templates.
2. WxReport.Svc's report worker picks up the PENDING language on its next cycle and calls Claude
   (`TemplateTranslator`) to translate the English baseline into it, with a per-token
   representability self-check and fail-closed validation. It resolves to one of:
   - **READY** — templates generated and structurally valid. *This is the precondition for QA
     (step 3) and for assigning recipients (step 5).*
   - **BLOCKED** — a token is not representable in the language; needs a renderer/code change, not
     a retry. Re-enabling requeues it.
   - **FAILED** — a transient error; retried automatically on a later cycle.

The Languages tab shows each supported language's status and operator guidance. Wait for
**READY** before continuing.

> READY means the templates *exist and pass the structural checks* — exact token set, `{n}`
> placeholders preserved, length/control-char rules. It does **not** mean they *read correctly*
> to a native speaker. That is exactly what step 3 audits.

---

## 3. Run the enable-time translation-QA harness

The harness drives the **real** report pipeline against two deliberately vocabulary-maximizing
exemplar scenarios (a warm/convective frontal passage and a winter/frozen variant), renders the
actual recipient email for English and the target language, assembles a judging request, and — via
the pluggable `IJudge` seam — has an **independent, non-Claude model** back-translate and critique
it. Two models with complementary blind spots: Claude self-checks at generation (WX-172), a
different model audits here.

Run in **Windows PowerShell** (not WSL — `dotnet` is on the Windows PATH) from the solution root
`C:\Code\HarderWare\WxServices`.

### Preferred: automated Gemini judge (one command)

**Precondition:** a Gemini API key (Google AI Studio, free tier) in the **InstallRoot** overlay
`C:\HarderWare\appsettings.local.json`:

```json
{ "Gemini": { "ApiKey": "AIza…", "Model": "gemini-2.5-flash" } }
```

`Model` is optional. The key is never committed or logged. The DB must be reachable and
`GlobalSettings.ClaudeApiKey` set (the generate phase still makes the real Claude reconciliation
calls).

```powershell
cd C:\Code\HarderWare\WxServices
dotnet run --project src\WxReport.Tools.TranslationQa -- --lang de --judge gemini
```

The en + de reports render, then the tool judges via Gemini and writes
`de.<stamp>.judged.json`. The **full** vocabulary is judged in one shot — no chunking, no paste.

**→ Full step-by-step, expected output, and error paths:
[`test-procedures/WX-227.md`](test-procedures/WX-227.md).**

### Fallback: manual paste into Copilot/ChatGPT

When no Gemini key is available, omit `--judge gemini`. The tool generates the judging request and
pre-creates a paste target, then prints a three-step cue card:

```powershell
dotnet run --project src\WxReport.Tools.TranslationQa -- --lang de
```

1. Open the `<iso>.<stamp>.request.md` file the tool prints — the human-readable judging request
   (a machine-readable `.request.json` sits beside it) — and paste its contents into **Copilot or
   ChatGPT** (any non-Claude model — the cross-model independence is the point).
2. Paste the model's full reply into the pre-created `de.<stamp>.response.txt` and save.
3. Parse the reply into the judged package:

   ```powershell
   dotnet run --project src\WxReport.Tools.TranslationQa -- --response "C:\HarderWare\translation-qa\<subfolder>\de.<stamp>.response.txt"
   ```

The `--response` phase touches neither the DB nor Claude — it only parses and validates the reply.

### Useful flags

| Flag | Meaning |
|------|---------|
| `--lang <iso>` | target language (required to generate), e.g. `de`, `es`, `eo`, `da` |
| `--scenario <name>` | `warm-convective` \| `winter-frozen` (default: both) |
| `--out <dir>` | output directory (default: `C:\HarderWare\translation-qa`) |
| `--judge gemini` | after generating, judge automatically via the Gemini API |
| `--response <file>` | parse a saved model reply instead of generating (manual fallback) |

### No-terminal alternative: the Rerun QA button

Once a package exists, an operator can regenerate it from **WxManager** (Translation-QA or
Vocabulary tab → **Rerun QA**, WX-235) without a shell or a redeploy. The button writes a request
row; WxReport.Svc's `QaRerunWorker` runs the same pipeline service-side and drops a fresh package.

---

## 4. Read the audit and adjudicate

The harness writes a **`{iso}.{stamp}` judge package** under `C:\HarderWare\translation-qa\` — the
human-readable `{iso}.{stamp}.request.md` judging request (with a machine-readable `.request.json`
beside it) paired with the `{iso}.{stamp}.judged.json` verdict, plus the rendered `en`/target
report HTML. Review it in **WxManager → Translation-QA**:

- The **triptych** per scenario: English reference report | target-language report (both rendered
  as the recipient email) | the judge's back-translation to English. Compare the English reference
  against the back-translation — divergence is your first signal.
- **Report findings** — awkward/incorrect passages, each with a location and a suggested fix.
- The **vocabulary-verdict table** — each token's verdict (`Ok` / `Warn` / `Wrong`, flagged rows
  first) with the judge's suggestion.

To act on a suggestion, use **Copy→DB** on that row. It promotes the suggested phrase into the
language's `LanguageTemplates`, stamps `ReviewedBy`/`ReviewedAtUtc`, and is refused if the
suggestion drops a `{n}` placeholder the English source has. **This is the human adjudication step —
the judge's output is advisory and is never auto-applied.**

**→ Full UI walkthrough: [`test-procedures/WX-219.md`](test-procedures/WX-219.md).**

### Reading the judge with appropriate suspicion

- Round-trip back-translation screens out gross errors and builds confidence; it is **not proof**
  and does not replace a native speaker. A back-translator can silently "repair" a clumsy
  translation.
- The judge is itself unverified and can confidently suggest a *worse* word — strongest for major
  languages, weakest for low-resource ones like Esperanto, exactly where help is most wanted. The
  judge's **self-reported confidence** (shown in the tab header) is the flag for when to discount
  it.

---

## 5. Assign recipients

With the vocabulary vetted, assign recipients to the language in **WxManager → Recipients**. The
language dropdown is gated on **READY**, so only fully-generated languages are selectable.

---

## Re-running

Re-run the harness (step 3) or press **Rerun QA** any time the templates change — after a Copy→DB
edit, a vocabulary edit in the Vocabulary tab, or a later prompt/renderer change that could move the
wording. Each run writes a fresh `{iso}.{stamp}` package; the Translation-QA tab shows the newest
first.

---

## Related and future work

- **WX-172** — Claude's generation-time representability *self*-check. Enable-time QA is the
  independent cross-model complement to it.
- **WX-173** — a fuller template-review round-trip. Today, acting on findings *is* the manual
  Copy→DB adjudication in step 4 (which stamps `ReviewedBy`/`ReviewedAtUtc`). When WX-173 lands, it
  can consume these verdicts more directly; this runbook will be updated to describe that handoff.
