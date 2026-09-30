#!/usr/bin/env python3
"""WX-506 rework (v1.61.7): find repeated rejections in the report service's log.

A "repeated rejection" (class B5 in WX-506.md) is an unscheduled cycle on which Claude was asked a
question it had already answered: an earlier cycle for the same locality was withheld ("WX-506
suppressed" or "WX-108 suppressed ... redundant") or judged not news ("Claude judged the ... arrival
not news"), and a later one, less than the window apart, ended the same way although its gate fired only criteria from the earlier one, with no send
in between and no logged reason to ask again. v1.61.7 skips the Claude call in that case ("WX-506
repeat skipped"), so after the deploy there should be none.

The code logs its reason to ask again ("WX-506 rejected-gate record not applied (<reason>)": a new
TAF or GFS run, new observed weather, a new prior, a severe onset, the window running out). Such a
line restarts the pairing for that locality, as does a weather report delivered to it ("report sent
(locality ..."; not the startup diagnostic report, and not a welcome, which clears the service's
record only on a cycle that also reconciled, a case this script cannot tell apart).
Because the service logs WindowExpired itself, no time tolerance is needed after the deploy;
--tolerance-minutes (default 0) exists only for reading logs from before it.

A withheld cycle's criteria are the gate line logged for that locality within the previous
10 minutes (the cycle's own gate line; a Claude call takes well under that). An older gate line
belongs to another cycle and is not used.

Reads one or more service logs, oldest first. For each locality it pairs a withheld update with the
gate criteria logged on that cycle ("WX-114 significance gate passed ... fired: ...").
Prints, from --since on:
  repeats: withheld=W  repeat_skipped=R  B5=N
  one "B5 <locality> <first time> -> <second time> <criteria>" line per repeated rejection
  one "calls <date> <n>" line per UTC date: Claude calls logged (LogClaudeTokens)

Usage:
  WX-506-repeats.py --since 'YYYY-MM-DD HH:MM:SS' [--window-hours 6] [--tolerance-minutes 15] --log FILE [--log FILE ...]
  WX-506-repeats.py --selftest
"""
from __future__ import annotations

import argparse
import re
import sys
from collections import Counter
from datetime import datetime, timedelta

STAMP = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)")
LOCALITY = re.compile(r"locality '([^']+)'")
GATE = re.compile(r"WX-114 significance gate passed \([^)]*\) — fired: (.*)\.\s*$")
WITHHELD = ("WX-506 suppressed", "WX-108 suppressed")
REDUNDANT_ONLY = "(redundant re-send)"  # of the WX-108 suppressions, only the redundant one is recorded
NOT_NEWS = re.compile(r"Claude judged the \S+ arrival not news")
SKIPPED = "WX-506 repeat skipped"
REASKED = "WX-506 rejected-gate record not applied"
DELIVERED = re.compile(r"(?<!\(diagnostic\) )report sent \(locality '([^']+)'")
GATE_MAX_AGE = timedelta(minutes=10)
CALL = "LogClaudeTokens"


def is_withheld(line: str) -> bool:
    """A cycle Claude answered without a send, which v1.61.7 records: withheld, or not news."""
    if WITHHELD[0] in line or NOT_NEWS.search(line):
        return True
    return WITHHELD[1] in line and REDUNDANT_ONLY in line


def scan(lines, since: datetime, window: timedelta):
    """(withheld, repeat_skipped, b5 list, calls per date) for the lines at or after `since`."""
    gate_by_loc: dict[str, tuple[datetime, frozenset[str]]] = {}
    last_withheld: dict[str, tuple[datetime, frozenset[str]]] = {}
    withheld = skipped = 0
    b5: list[tuple[str, datetime, datetime, frozenset[str]]] = []
    calls: Counter[str] = Counter()
    for line in lines:
        m = STAMP.match(line)
        if not m:
            continue
        at = datetime.strptime(m.group(1), "%Y-%m-%d %H:%M:%S")
        if at < since:
            continue
        loc_m = LOCALITY.search(line)
        loc = loc_m.group(1) if loc_m else None
        if CALL in line:
            calls[m.group(1)[:10]] += 1
        d = DELIVERED.search(line)
        if d:
            last_withheld.pop(d.group(1), None)
            continue
        if loc is None:
            continue
        g = GATE.search(line)
        if g:
            gate_by_loc[loc] = (at, frozenset(c.strip() for c in g.group(1).split(",")))
            continue
        if SKIPPED in line:
            skipped += 1
            gate_by_loc.pop(loc, None)
            continue
        if REASKED in line:
            last_withheld.pop(loc, None)
            continue
        if is_withheld(line):
            withheld += 1
            gate = gate_by_loc.pop(loc, None)   # this cycle's gate line, used once
            fired = gate[1] if gate and at - gate[0] <= GATE_MAX_AGE else frozenset()
            prev = last_withheld.get(loc)
            if prev and fired and fired <= prev[1] and at - prev[0] < window:
                b5.append((loc, prev[0], at, fired))
            if fired:
                last_withheld[loc] = (at, fired)
    return withheld, skipped, b5, calls


def report(result) -> list[str]:
    withheld, skipped, b5, calls = result
    out = [f"repeats: withheld={withheld}  repeat_skipped={skipped}  B5={len(b5)}"]
    for loc, first, second, fired in b5:
        out.append(f"B5 {loc} {first:%Y-%m-%d %H:%M} -> {second:%Y-%m-%d %H:%M} {', '.join(sorted(fired))}")
    for date in sorted(calls):
        out.append(f"calls {date} {calls[date]}")
    return out


def selftest() -> int:
    t = "2026-10-01"
    gate = "DEBUG [ReportWorker.cs::ProcessLocalityAsync:1211] locality 'Austin, TX' (Id=2): WX-114 significance gate passed (Enforce, metar) — fired: {}."
    held = "INFO  [ReportWorker.cs::ProcessLocalityAsync:1351] locality 'Austin, TX' (Id=2): WX-506 suppressed metar send — no computed change."
    call = "DEBUG [ReportWorker.cs::LogClaudeTokens:1814] locality 'Austin, TX' (Id=2): Claude tokens [metar/reconciled] — in=1 out=1 cache-read=0 cache-write=0."
    skip = "INFO  [ReportWorker.cs::ProcessLocalityAsync:1230] locality 'Austin, TX' (Id=2): WX-506 repeat skipped metar cycle — criteria rejected 60 min ago; Claude not called."
    other = "DEBUG [ReportWorker.cs::ProcessLocalityAsync:1211] locality 'Spring, TX' (Id=3): WX-114 significance gate passed (Enforce, metar) — fired: {}."
    other_held = held.replace("'Austin, TX' (Id=2)", "'Spring, TX' (Id=3)")
    redundant = "INFO  [ReportWorker.cs::ProcessLocalityAsync:1351] locality 'Austin, TX' (Id=2): WX-108 suppressed metar send — reconciled snapshot is materially identical to the last sent report (redundant re-send). Trace: x"
    flip = "INFO  [ReportWorker.cs::ProcessLocalityAsync:1351] locality 'Austin, TX' (Id=2): WX-108 suppressed metar send — severe-flag de-escalation on an observation-only advance. Trace: this would be redundant"
    diagnostic = "INFO  [ReportWorker.cs::SendStartupDiagnosticAsync:420] paul_en x@y (Paul): startup (diagnostic) report sent (locality 'Austin, TX' (Id=2))."
    reask = "DEBUG [ReportWorker.cs::ProcessLocalityAsync:1240] locality 'Austin, TX' (Id=2): WX-506 rejected-gate record not applied (NewGuidance) — calling Claude."
    sent = "INFO  [ReportWorker.cs::DeliverWeatherReportAsync:1726] paul_en x@y (Paul): report sent (locality 'Austin, TX' (Id=2))."
    welcome = "INFO  [ReportWorker.cs::SendWelcomeAsync:1845] new_en x@y (New): welcome sent (locality 'Austin, TX')."
    notnews = "INFO  [ReportWorker.cs::ProcessLocalityAsync:1246] locality 'Austin, TX' (Id=2): Claude judged the metar arrival not news — no send. Trace: x"
    A = "precip-remove@T1(10-01 11Z)"
    B = "precip-add@T1(10-02 05Z)"
    w = timedelta(hours=6)
    since = datetime(2026, 10, 1)
    cases = [
        ("same criteria 1 h apart is B5", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:00:30.000 {call}", f"{t} 10:01:00.000 {held}",
            f"{t} 11:00:00.000 {gate.format(A)}", f"{t} 11:00:30.000 {call}", f"{t} 11:01:00.000 {held}"], 1, 2, 0, 2),
        ("same criteria 6 h apart is not B5", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {held}",
            f"{t} 16:01:00.000 {gate.format(A)}", f"{t} 16:01:30.000 {held}"], 0, 2, 0, 0),
        ("different criteria is not B5", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {held}",
            f"{t} 11:00:00.000 {gate.format(A + ', ' + B)}", f"{t} 11:01:00.000 {held}"], 0, 2, 0, 0),
        ("criteria order does not matter", [
            f"{t} 10:00:00.000 {gate.format(A + ', ' + B)}", f"{t} 10:01:00.000 {held}",
            f"{t} 11:00:00.000 {gate.format(B + ', ' + A)}", f"{t} 11:01:00.000 {held}"], 1, 2, 0, 0),
        ("another locality between does not break the pairing", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {held}",
            f"{t} 10:30:00.000 {other.format(B)}", f"{t} 10:31:00.000 {other_held}",
            f"{t} 11:00:00.000 {gate.format(A)}", f"{t} 11:01:00.000 {held}"], 1, 3, 0, 0),
        ("two localities with the same criteria are not a repeat of each other", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {held}",
            f"{t} 10:30:00.000 {other.format(A)}", f"{t} 10:31:00.000 {other_held}"], 0, 2, 0, 0),
        ("a skip is counted and is not a withheld update", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {held}",
            f"{t} 11:00:00.000 {gate.format(A)}", f"{t} 11:00:01.000 {skip}"], 0, 1, 1, 0),
        ("a subset of the earlier criteria is B5", [
            f"{t} 10:00:00.000 {gate.format(A + ', ' + B)}", f"{t} 10:01:00.000 {held}",
            f"{t} 11:00:00.000 {gate.format(A)}", f"{t} 11:01:00.000 {held}"], 1, 2, 0, 0),
        ("a WX-108 redundant withhold counts, a severe flip does not", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {redundant}",
            f"{t} 11:00:00.000 {gate.format(A)}", f"{t} 11:01:00.000 {flip}"], 0, 1, 0, 0),
        ("a redundant withhold then a no-change withhold is B5", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {redundant}",
            f"{t} 11:00:00.000 {gate.format(A)}", f"{t} 11:01:00.000 {held}"], 1, 2, 0, 0),
        ("a logged re-ask reason restarts the pairing", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {held}",
            f"{t} 11:00:00.000 {gate.format(A)}", f"{t} 11:00:00.500 {reask}", f"{t} 11:01:00.000 {held}"], 0, 2, 0, 0),
        ("a report sent in between restarts the pairing", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {held}",
            f"{t} 12:00:00.000 {sent}",
            f"{t} 13:00:00.000 {gate.format(A)}", f"{t} 13:01:00.000 {held}"], 0, 2, 0, 0),
        ("a welcome sent in between does not restart the pairing", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {held}",
            f"{t} 10:30:00.000 {welcome}",
            f"{t} 11:00:00.000 {gate.format(A)}", f"{t} 11:01:00.000 {held}"], 1, 2, 0, 0),
        ("the startup diagnostic report does not restart the pairing", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {held}",
            f"{t} 10:30:00.000 {diagnostic}",
            f"{t} 11:00:00.000 {gate.format(A)}", f"{t} 11:01:00.000 {held}"], 1, 2, 0, 0),
        ("a severe flip whose trace says redundant is not a recorded withhold", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {flip}",
            f"{t} 11:00:00.000 {gate.format(A)}", f"{t} 11:01:00.000 {held}"], 0, 1, 0, 0),
        ("a gate line older than 10 minutes is another cycle's", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {held}",
            f"{t} 10:40:00.000 {gate.format(A)}",
            f"{t} 11:01:00.000 {held}"], 0, 2, 0, 0),
        ("a withhold with no gate line on its cycle does not borrow an old one", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {held}",
            f"{t} 11:01:00.000 {held}"], 0, 2, 0, 0),
        ("just inside the window is B5, at the window is not", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {held}",
            f"{t} 15:59:00.000 {gate.format(A)}", f"{t} 15:59:30.000 {held}",
            f"{t} 21:59:00.000 {gate.format(A)}", f"{t} 22:00:00.000 {held}"], 1, 3, 0, 0),
        ("a not-news answer then a withheld update on the same criteria is B5", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {notnews}",
            f"{t} 11:00:00.000 {gate.format(A)}", f"{t} 11:01:00.000 {held}"], 1, 2, 0, 0),
        ("two not-news answers on the same criteria are B5", [
            f"{t} 10:00:00.000 {gate.format(A)}", f"{t} 10:01:00.000 {notnews}",
            f"{t} 11:00:00.000 {gate.format(A)}", f"{t} 11:01:00.000 {notnews}"], 1, 2, 0, 0),
        ("lines before --since are ignored", [
            f"2026-09-30 23:00:00.000 {gate.format(A)}", f"2026-09-30 23:01:00.000 {held}",
            f"{t} 00:30:00.000 {gate.format(A)}", f"{t} 00:31:00.000 {held}"], 0, 1, 0, 0),
    ]
    ok = 0
    for name, lines, want_b5, want_held, want_skip, want_calls in cases:
        withheld, skipped, b5, calls = scan(lines, since, w)
        got = (len(b5), withheld, skipped, sum(calls.values()))
        want = (want_b5, want_held, want_skip, want_calls)
        good = got == want
        ok += good
        print(f"{'ok  ' if good else 'FAIL'} {name}: (B5, withheld, skipped, calls) = {got}, want {want}")
    print(f"{ok}/{len(cases)} as expected")
    return 0 if ok == len(cases) else 1


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--since", help="window start, 'YYYY-MM-DD HH:MM:SS' (UTC, as the log writes it)")
    ap.add_argument("--window-hours", type=float, default=6.0)
    ap.add_argument("--tolerance-minutes", type=float, default=0.0,
                    help="shortens the window, for logs from before v1.61.7 only (log times trail the cycle's)")
    ap.add_argument("--log", action="append", default=[], help="a service log; repeat, oldest first")
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args(argv)
    if args.selftest:
        return selftest()
    if not args.since or not args.log:
        ap.error("--since and at least one --log are required")
    since = datetime.strptime(args.since, "%Y-%m-%d %H:%M:%S")
    lines = []
    for path in args.log:
        with open(path, encoding="utf-8", errors="replace") as f:
            lines.extend(f)
    window = timedelta(hours=args.window_hours) - timedelta(minutes=args.tolerance_minutes)
    for line in report(scan(lines, since, window)):
        print(line)
    return 0


if __name__ == "__main__":
    sys.exit(main())
