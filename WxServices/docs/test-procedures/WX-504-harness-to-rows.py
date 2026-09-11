#!/usr/bin/env python3
"""WX-504: turn the QA harness's *.reconciled.json files into analyze.py's row format.

Usage:  harness_to_rows.py <arm-label> <dir-with-reconciled-json>... > rows.jsonl
Each reconciled file carries every narrative language of one reconcile, so it becomes one row per
language. Ids are synthetic and unique per file, which is what analyze.py keys its dedup on.
"""
import glob, json, os, sys

arm, dirs = sys.argv[1], sys.argv[2:]
files = sorted(f for d in dirs for f in glob.glob(os.path.join(d, "**", "*.reconciled.json"), recursive=True))
if not files:
    sys.exit(f"no *.reconciled.json under {dirs} - refusing to emit an empty corpus")
n = 0
for i, f in enumerate(files, 1):
    raw = json.load(open(f, encoding="utf-8"))
    report = raw["structuredReport"]
    for lang in report["narrative"]:
        n += 1
        print(json.dumps({
            "Id": i * 10 + len(lang) % 10 + n, "RecipientId": f"{arm}_{lang}", "IsoCode": lang,
            "ForecastSnapshotId": i, "LocalityTz": raw["tz"], "SentAtUtc": raw["anchorUtc"],
            "SnapshotBody": json.dumps(raw["finalSnapshot"]), "StructuredReport": json.dumps(report),
            "SourceFile": os.path.basename(f),
        }, ensure_ascii=False))
print(f"{arm}: {len(files)} reconciled files -> {n} rows", file=sys.stderr)