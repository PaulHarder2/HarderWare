#!/usr/bin/env python3
"""WX-506 screen: pair every "Why this update" band with the computed changes it was written from.

Input: the JSON-lines corpus written by WX-504-dump.ps1 (one sent CommittedSend per line).

For each LOCALITY CYCLE (one ForecastSnapshotId) whose structured report carries computed changes,
it prints the changes — phenomenon, direction, tier, local window — beside every language's band, so
a reader can judge each band against exactly the facts it was given. It is a pairing aid, not a
verdict: whether a band names only its listed changes is judged by hand (WX-506.md step 6).

For each banded cycle it also prints the command that shows the FACTS the band call was given (the prior →
now values a band may state), with the prior snapshot filled in: the locality's latest earlier non-diagnostic
send, taken as the newest earlier snapshot any recipient of this cycle was sent. A cycle whose prior was sent
before the corpus begins prints "prior not in corpus" — dump from before --since to avoid it.

It also counts the band call's log fingerprints in wxreport-svc.log files, when given:
  written   "Change band written by the band call"
  fallback  "deterministic change band (WX-506)"

Usage:
  WX-506-screen.py CORPUS.jsonl [--since 'YYYY-MM-DD HH:MM:SS'] [--log FILE ...]
  WX-506-screen.py --selftest

Exit: 0 ran · 1 selftest failure · 2 usage or unreadable input
"""
import json
import sys
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

PARTS = ["early hours", "morning", "afternoon", "evening"]
WRITTEN = "Change band written by the band call"
FALLBACK = "deterministic change band (WX-506)"


def parse_utc(s):
    return datetime.strptime(s.rstrip("Z")[:19], "%Y-%m-%dT%H:%M:%S").replace(tzinfo=timezone.utc)


def label(utc, tz):
    local = utc.astimezone(tz)
    return f"{local:%a %Y-%m-%d} {PARTS[local.hour // 6]}"


def window_label(window, tz):
    start = parse_utc(window["startUtc"])
    # endUtc is exclusive; the last block starts at least one second before it.
    last = parse_utc(window["endUtc"]) - timedelta(seconds=1)
    a, b = label(start, tz), label(last, tz)
    return a if a == b else f"{a} through {b}"


def prior_snapshot(rows, sid):
    """The prior snapshot id for cycle `sid`: the newest snapshot, other than `sid`, sent non-diagnostically to any
    of its recipients before `sid` was first sent (ReportWorker's locality baseline). None when not in the corpus."""
    sent = lambda r: (r.get("SentAtUtc") or "").replace("T", " ")[:19]
    own = [r for r in rows if r.get("ForecastSnapshotId") == sid and sent(r)]
    if not own:
        return None
    first = min(sent(r) for r in own)
    members = {r.get("RecipientId") for r in own}
    earlier = [r["ForecastSnapshotId"] for r in rows
               if r.get("RecipientId") in members and sent(r) and sent(r) < first
               and r.get("ForecastSnapshotId") != sid and r.get("IsDiagnostic") not in (True, 1, "1", "True")]
    return max(earlier) if earlier else None


def facts_command(sid, prior, tz, langs, sent):
    if prior is None:
        return "prior not in corpus — dump from earlier than --since"
    return ("dotnet run --project src\\WxReport.Tools.TranslationQa -- --replay-band --facts-only "
            f"--prior {prior} --final {sid} --tz {tz} --langs {','.join(sorted(langs))}")


def cycles(rows, since=None):
    """Group sent rows into locality cycles: {snapshotId: {sent, tz, changes, bands{lang: text|None}}}.
    With `since` ('YYYY-MM-DD HH:MM:SS' UTC), rows sent before it are dropped, so the cycle counts and the log
    counts cover the same window."""
    out = {}
    for r in rows:
        sent = (r.get("SentAtUtc") or "").replace("T", " ")[:19]
        if not sent:
            continue   # never sent: not a cycle a recipient saw (the dump selects sent rows; this is defensive)
        if since and sent < since:
            continue
        if r.get("IsDiagnostic") in (True, 1, "1", "True"):
            continue
        report = r.get("StructuredReport")
        if not report:
            continue
        body = json.loads(report)
        changes = body.get("changes") or []
        if not changes:
            continue
        sid = r["ForecastSnapshotId"]
        c = out.setdefault(sid, {"sent": r.get("SentAtUtc"), "tz": None, "changes": changes, "bands": {}, "langs": set()})
        c["tz"] = c["tz"] or r.get("LocalityTz") or r.get("RecipientTz")
        if r.get("IsoCode"):
            c["langs"].add(r["IsoCode"])
        if (r.get("SentAtUtc") or "") < (c["sent"] or "~"):
            c["sent"] = r.get("SentAtUtc")
        for lang, sections in (body.get("narrative") or {}).items():
            c["bands"][lang] = sections.get("changeSummary")
    return out


def count_log(paths, since):
    """Returns (written, fallback, earliest): earliest is the oldest timestamp seen in any file, so a caller can
    tell whether the logs reach back to `since` at all — a count over rotated-away days reads as a real gap."""
    written = fallback = 0
    earliest = None
    for p in paths:
        with open(p, encoding="utf-8", errors="replace") as f:
            for line in f:
                stamp = line[:19]
                if len(stamp) == 19 and stamp[4] == "-" and stamp[10] == " " and (earliest is None or stamp < earliest):
                    earliest = stamp
                if since and stamp < since:
                    continue
                if WRITTEN in line:
                    written += 1
                elif FALLBACK in line:
                    fallback += 1
    return written, fallback, earliest


def report(cyc, write=print, rows=()):
    banded = sum(1 for c in cyc.values() if any(v for v in c["bands"].values()))
    fell_back = len(cyc) - banded
    write(f"cycles_with_changes={len(cyc)}  with_band={banded}  fallback_or_no_band={fell_back}")
    for sid, c in sorted(cyc.items()):
        tz = ZoneInfo(c["tz"] or "UTC")
        write("")
        write(f"snapshot {sid}  sent {c['sent']}  tz {c['tz']}")
        for i, ch in enumerate(c["changes"], 1):
            q = ", ".join(f"{x.get('kind')} {x.get('value')}" for x in (ch.get("quantities") or []))
            write(f"  change {i}: {ch['phenomenon']} {ch['direction']} (tier {ch['tier']}) — {window_label(ch['window'], tz)}"
                  + (f" — now: {q}" if q else ""))
        for lang, text in sorted(c["bands"].items()):
            write(f"  band [{lang}]: {text if text else '(none — deterministic fallback band)'}")
        if rows and any(c["bands"].values()):
            langs = c["langs"] or set(c["bands"])
            write(f"  facts: {facts_command(sid, prior_snapshot(rows, sid), c['tz'], langs, c['sent'])}")
    return banded, fell_back


def selftest():
    ok = total = 0

    def check(name, cond):
        nonlocal ok, total
        total += 1
        ok += bool(cond)
        print(("ok   " if cond else "FAIL ") + name)

    change = {"tier": "plans", "phenomenon": "rain", "direction": "appearing",
              "window": {"startUtc": "2026-09-16T17:00:00Z", "endUtc": "2026-09-16T23:00:00Z"}}
    two_block = dict(change, window={"startUtc": "2026-09-15T23:00:00Z", "endUtc": "2026-09-16T11:00:00Z"})

    def row(sid, lang, band, changes=(change,), diag=False, tz="America/Chicago"):
        return {"ForecastSnapshotId": sid, "SentAtUtc": "2026-09-15T16:29:38Z", "IsDiagnostic": diag,
                "LocalityTz": tz, "StructuredReport": json.dumps(
                    {"schemaVersion": 5, "changes": list(changes),
                     "narrative": {lang: {"changeSummary": band, "closing": "x"}}})}

    chicago = ZoneInfo("America/Chicago")
    check("single-block window label", window_label(change["window"], chicago) == "Wed 2026-09-16 afternoon")
    check("midnight-crossing window names both ends",
          window_label(two_block["window"], chicago) == "Tue 2026-09-15 evening through Wed 2026-09-16 early hours")

    cyc = cycles([row(1, "en", "Rain is now possible Wednesday afternoon."), row(1, "es", "Lluvia posible."),
                  row(2, "en", None), row(3, "en", "x", changes=()), row(4, "en", "x", diag=True)])
    check("rows of one snapshot collapse to one cycle, bands per language", cyc[1]["bands"] == {
        "en": "Rain is now possible Wednesday afternoon.", "es": "Lluvia posible."})
    check("a cycle whose band fell back is kept (null band)", 2 in cyc and cyc[2]["bands"] == {"en": None})
    check("a report with no changes is excluded", 3 not in cyc)
    check("a diagnostic send is excluded", 4 not in cyc)
    unsent = row(6, "en", "x")
    unsent["SentAtUtc"] = None
    check("a row with no SentAtUtc is not a cycle", cycles([unsent]) == {})
    check("--since drops cycles sent before it, as it drops log lines", cycles(
        [row(5, "en", "x")], since="2026-09-15 17:00:00") == {})

    base = [dict(row(9, "en", "x", changes=()), RecipientId=8, SentAtUtc="2026-09-15T09:00:00Z"),
            dict(row(10, "en", "x", changes=()), RecipientId=7, SentAtUtc="2026-09-15T10:00:00Z"),
            dict(row(11, "en", "x", diag=True), RecipientId=7, SentAtUtc="2026-09-15T12:00:00Z"),
            dict(row(12, "en", "x"), RecipientId=8, SentAtUtc="2026-09-15T14:00:05Z"),
            dict(row(12, "en", "x"), RecipientId=7, SentAtUtc="2026-09-15T14:00:00Z"),
            dict(row(13, "en", "x", changes=()), RecipientId=9, SentAtUtc="2026-09-15T13:00:00Z")]
    check("prior is the newest earlier non-diagnostic send to a member", prior_snapshot(base, 12) == 10)
    check("no earlier send in the corpus gives no prior", prior_snapshot(base, 9) is None)
    flines = []
    report(cycles(base), flines.append, rows=base)
    check("a banded cycle prints its facts command with the prior filled in", any(
        l.endswith("--replay-band --facts-only --prior 10 --final 12 --tz America/Chicago --langs en")
        for l in flines))

    lines = []
    banded, fell_back = report(cyc, lines.append)
    check("counts: one banded cycle, one fallback", (banded, fell_back) == (1, 1))
    qrow = row(20, "en", "x", changes=[dict(change, phenomenon="temperature", direction="strengthening",
                                             quantities=[{"kind": "temp", "value": 38}, {"kind": "temp", "value": 27.4}])])
    qlines = []
    report(cycles([qrow]), qlines.append)
    check("a change's stored quantities are printed", any(l.endswith("— now: temp 38, temp 27.4") for l in qlines))
    check("band printed beside its change", any("band [en]: Rain is now possible" in l for l in lines)
          and any("change 1: rain appearing (tier plans) — Wed 2026-09-16 afternoon" in l for l in lines))

    import tempfile
    import os
    with tempfile.NamedTemporaryFile("w", delete=False, suffix=".log", encoding="utf-8") as f:
        f.write("2026-09-15 10:00:00.000 I x: Change band written by the band call from 1 computed change(s)\n")
        f.write("2026-09-15 12:00:00.000 W x: Change-band call failed; the report falls back to the deterministic change band (WX-506).\n")
        f.write("2026-09-16 09:00:00.000 I x: Change band written by the band call from 2 computed change(s)\n")
        path = f.name
    try:
        check("log counts with no boundary", count_log([path], None)[:2] == (2, 1))
        check("log counts respect --since", count_log([path], "2026-09-15 11:00:00")[:2] == (1, 1))
        check("earliest log timestamp is reported", count_log([path], None)[2] == "2026-09-15 10:00:00")
    finally:
        os.unlink(path)

    print(f"{ok}/{total} as expected")
    return 0 if ok == total else 1


def main(argv):
    if argv[:1] == ["--selftest"]:
        return selftest()
    if not argv or argv[0].startswith("--"):
        print(__doc__, file=sys.stderr)
        return 2
    corpus, rest = argv[0], argv[1:]
    since, logs = None, []
    i = 0
    while i < len(rest):
        if rest[i] == "--since" and i + 1 < len(rest):
            since, i = rest[i + 1], i + 2
        elif rest[i] == "--log" and i + 1 < len(rest):
            logs.append(rest[i + 1]); i += 2
        else:
            print(f"unknown argument: {rest[i]}", file=sys.stderr)
            return 2
    try:
        with open(corpus, encoding="utf-8-sig") as f:
            rows = [json.loads(line) for line in f if line.strip()]
    except (OSError, ValueError) as e:
        print(f"cannot read corpus: {e}", file=sys.stderr)
        return 2
    cyc = cycles(rows, since)
    report(cyc, rows=rows)
    if logs:
        # The window starts at --since, or else at the earliest cycle in the corpus: coverage asks whether the logs
        # reach back that far. It cannot see a day missing from the MIDDLE of the rotation; rotation drops the
        # oldest file, so that gap is not expected.
        start = since or min((c["sent"] or "").replace("T", " ")[:19] for c in cyc.values()) if cyc else since
        written, fallback, earliest = count_log(logs, start)
        coverage = "complete" if start and earliest and earliest <= start else "INCOMPLETE — pass older rotated logs"
        print(f"\nlog: window_start={start}  band_written={written}  band_fallback={fallback}  earliest={earliest}  coverage={coverage}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
