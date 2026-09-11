#!/usr/bin/env python3
"""WX-340 / WX-504 classes 1-4: screen rendered narrative prose against the snapshot it was generated from.

Procedure: docs/test-procedures/WX-504.md.

A SCREEN, not a verdict. Every hit is a CANDIDATE to be judged by hand against the grid, as class 5
was. Run --selftest first: it must flag every planted violation and nothing in the clean twins.

Classes emitted:
  C1   precipitation asserted at a time whose blocks are all dry (not negated, not about the prior)
  C2   an aggregate period (weekend / week / next days) called dry while a day in it is wet
  C2h  the same, hedged ("mostly dry") - reported separately; whether it counts is a ruling
  C3   a storm-family word ASSERTED for a window with no severe block
  C3p  a storm-family word used only about the PRIOR / removed forecast - reported separately
  C4   a change window spanning two local dates, narrated by its tail date only

Usage:  analyze.py corpus.jsonl            -> candidates + counts (+ corpus.jsonl.candidates.json)
        analyze.py --selftest              -> exit 0 only if the fixture behaves exactly as expected
        The corpus run also writes corpus.jsonl.sample.json: up to 10 UNFLAGGED reports per language,
        chosen by a fixed seed, for the hand-read that measures what the screen misses.
"""
import json, re, sys, hashlib, random
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

def tz_of(name):
    try:
        return ZoneInfo(name)
    except Exception:
        raise SystemExit(f"UNKNOWN TIMEZONE {name!r} - refusing to guess")

def part_of(h):  # mirrors StructuredReportRenderer.PartOf (verified 2026-09-11)
    return "early" if h < 6 else "morning" if h < 12 else "afternoon" if h < 18 else "evening"

# ---------- per-language lexicons (screen only; a miss here is a screening limit) ----------
WEEKDAYS = {  # index 0 = Monday
    "en": [r"monday", r"tuesday", r"wednesday", r"thursday", r"friday", r"saturday", r"sunday"],
    "es": [r"lunes", r"martes", r"mi[eé]rcoles", r"jueves", r"viernes", r"s[aá]bado", r"domingo"],
    "de": [r"montag", r"dienstag", r"mittwoch", r"donnerstag", r"freitag", r"samstag", r"sonntag"],
    "eo": [r"lund", r"mard", r"merkred", r"ĵaŭd", r"vendred", r"sabat", r"dimanĉ"],
}
WD_SUFFIX = {"en": r"(?:'s|s)?\b", "es": r"\b",
             "de": r"(?:s)?(?:morgen|vormittag|mittag|nachmittag|abend|nacht|früh)?s?\b",
             "eo": r"(?:on|o|e)\b"}
DAYPART = {
    "en": [(r"early (\w+ ){0,2}morning|early hours|overnight|pre-?dawn", "early"), (r"\bnight\b", "night"), (r"morning", "morning"),
           (r"afternoon", "afternoon"), (r"evening", "evening")],
    "es": [(r"madrugada|temprano por la ma[ñn]ana|por la ma[ñn]ana temprano", "early"), (r"por la ma[ñn]ana|la ma[ñn]ana del", "morning"),
           (r"tarde(?!-noche)", "afternoon"), (r"noche", "evening")],
    "de": [(r"frühen morgen(stunden)?|früh am morgen", "early"), (r"nacht", "night"), (r"am morgen|vormittag", "morning"),
           (r"nachmittag", "afternoon"), (r"abend", "evening")],
    "eo": [(r"fruaj horoj|noktomez|frumaten\w*|fruaj? maten\w*|frue maten\w*", "early"), (r"nokt", "night"), (r"maten", "morning"),
           (r"posttagmez", "afternoon"), (r"vesper", "evening")],
}
RELATIVE = {  # (pattern, day offset, part or None)
    "en": [(r"\btonight\b", 0, "evening"), (r"\btoday\b", 0, None), (r"\btomorrow\b", 1, None)],
    "es": [(r"esta noche", 0, "evening"), (r"\bhoy\b", 0, None), (r"pasado ma[ñn]ana", 2, None)],
    "de": [(r"heute abend", 0, "evening"), (r"heute (morgen|früh|vormittag)", 0, "morning"),
           (r"heute nachmittag", 0, "afternoon"), (r"heute nacht", 0, "night"),
           (r"\bheute\b(?!\s+(abend|morgen|früh|vormittag|nachmittag|nacht))", 0, None),
           (r"(?<!am )(?<!frühen )(?<!heute )(?<!gegen )(?<!den )(?<!zum )\bmorgen\b(?!\w)", 1, None)],
    "eo": [(r"ĉi-?vespere|ĉi-?nokte", 0, "evening"), (r"\bhodiaŭ\b", 0, None), (r"\bmorgaŭ\b", 1, None)],
}
PRECIP = {"en": r"\b(rain\w*|snow\w*|sleet|freezing rain|wintry mix|precipitation|wet)\b",
          "es": r"\b(lluvi\w*|llov\w*|nieve|nevad\w*|aguanieve|precipitaci\w*)\b",
          "de": r"(regen\w*|schnee\w*|niederschl\w*|graupel|schauer)",
          "eo": r"\b(pluv\w*|neĝ\w*|precipitaĵ\w*)\b"}
DRY = {"en": r"\b(dry|drier|rain-free|clear|quiet)\b",
       "es": r"\b(sec[oa]s?|sin lluvia|despejad[oa]s?|tranquil[oa]s?)\b",
       "de": r"(trocken\w*|niederschlagsfrei|\bklar\w*|\bruhig\w*)",
       "eo": r"\b(seka\w*|senpluv\w*|klara\w*|trankvil\w*)\b"}
# C1's own exclusion: a precipitation word negated INSIDE the phrase ("rain-free"). Deliberately not
# DRY, whose aggregate words ("dry", "quiet") often describe ANOTHER day in the same sentence.
PRECIP_FREE = {"en": r"\brain-free\b", "es": r"\bsin lluvia\b",
               "de": r"(regenfrei\w*|niederschlagsfrei\w*)", "eo": r"\bsenpluv\w*"}
HEDGE = {"en": r"\b(mostly|largely|generally|mainly)\s+(\w+\s+)?", "es": r"\b(mayormente|mayoritariamente|en general)\s+",
         "de": r"\b(überwiegend|weitgehend|meist)\s+", "eo": r"\b(plejparte|ĝenerale)\s+"}
NEG = {"en": r"\b(no|not|without|ends?|ending|clears?|clearing|tapers?|stops?|winds? down)\b",
       "es": r"\b(no|sin|termin\w*|cesa\w*|despej\w*|se retira\w*)\b",
       "de": r"\b(kein\w*|nicht|ohne|endet|klingt\w*|abklingt|hört\w*|zieht\w* ab)\b",
       "eo": r"\b(ne|neniu|sen|ĉes\w*|finiĝ\w*)\b"}
PRIOR = {"en": r"\b(removed|dropped|drops|cleared|eliminat\w*|off the table|no longer|prior|previous\w*|earlier|had been|what was|was (expected|forecast)|were (expected|forecast)|eased|revised downward)\b",
         "es": r"\b(eliminad\w*|retirad\w*|anterior\w*|previamente|desaparec\w*|antes se|ya no)\b",
         "de": r"\b(entfall\w*|gestrichen|vorher\w*|zuvor|bisherig\w*|nicht mehr)\b",
         "eo": r"\b(forig\w*|antaŭ\w*|ne plu)\b"}
STORM = {"en": r"\b(storms?|stormy|thunderstorms?|thunder\w*|squalls?|convective (signal|energy)\w*)\b",
         "es": r"\b(tormentas?|tempestad\w*|tronadas?|se[ñn]al(es)? convectiv\w*|energ[ií]a convectiva)\b",
         "de": r"(gewitter\w*|unwetter\w*|konvektiv\w* (signal|energie)\w*)",
         "eo": r"\b(fulmotondr\w*|ŝtorm\w*|tondr\w*|konvekci\w* signal\w*)\b"}
AGGREGATE = {"en": [(r"\bweekend\b", "weekend"), (r"\bweek\b|next (few|several|couple of) days", "all")],
             "es": [(r"fin de semana", "weekend"), (r"\bsemana\b|pr[oó]ximos d[ií]as", "all")],
             "de": [(r"wochenende", "weekend"), (r"\bwoche\b|nächsten tage", "all")],
             "eo": [(r"semajnfin\w*", "weekend"), (r"\bsemajno\b|venontaj tagoj", "all")]}
REST = {"en": r"\brest of\b", "es": r"\bresto del?\b", "de": r"\brest (des|der)\b", "eo": r"\bcetero\b"}
UNTIL = {"en": r"\b(through|until|till)\s+", "es": r"\bhasta (el )?", "de": r"\bbis (einschließlich |zum |am )?",
         "eo": r"\bĝis\s+"}

# The day-part a German weekday compound's tail names. "nacht" is the night FOLLOWING the day.
DE_TAIL = {"morgen": "morning", "vormittag": "morning", "mittag": "afternoon", "nachmittag": "afternoon",
           "abend": "evening", "nacht": "night", "früh": "earlymorning"}

QTIME = re.compile(r"\{q:time:([0-9T:\-]+Z)\}")
TOKEN = re.compile(r"\{[^}]*\}")

def lowered_prose(text):
    """Lowercased prose with every token blanked except {q:time:...}, which carries a real instant."""
    return TOKEN.sub(lambda m: m.group(0) if m.group(0).startswith("{q:time") else " ", text).lower()

def sentences(text):
    return [s.strip() for s in re.split(r"(?<=[.!?])\s+", text or "") if s.strip()]

def clauses(low):
    return [c for c in re.split(r"\s[—–-]\s|[;:,]", low) if c.strip()]

def utc(s):
    return datetime.fromisoformat(s.replace("Z", "+00:00"))

class Grid:
    def __init__(self, body, tz, sent_utc):
        self.tz = tz
        self.blocks = []
        for b in sorted(json.loads(body)["blocks"], key=lambda x: x["startUtc"]):
            s = utc(b["startUtc"])
            loc = s.astimezone(tz)
            self.blocks.append(dict(start=s, date=loc.date(), part=part_of(loc.hour),
                                    wet=b.get("precipExpectation", "none") != "none",
                                    severe=bool(b.get("severeFlag")), phen=b.get("precipPhenomenon")))
        # A block ends where the next begins: 5 or 7 UTC hours on a DST day, never a fixed 6.
        for cur, nxt in zip(self.blocks, self.blocks[1:]):
            cur["end"] = nxt["start"]
        if self.blocks:  # the last block has no successor: its end is the next local day-part boundary
            loc = self.blocks[-1]["start"].astimezone(tz).replace(tzinfo=None)
            self.blocks[-1]["end"] = (loc + timedelta(hours=6)).replace(tzinfo=tz).astimezone(timezone.utc)
        self.today = sent_utc.astimezone(tz).date()
        self.dates = sorted({b["date"] for b in self.blocks})
    def by_date_part(self, date, part):
        return [b for b in self.blocks if b["date"] == date and (part is None or b["part"] == part)]
    def at(self, t):
        return [b for b in self.blocks if b["start"] <= t < b["end"]]
    def in_window(self, s, e):
        return [b for b in self.blocks if s <= b["start"] < e]
    def weekday_date(self, idx):  # first GRID date with that weekday - anchored to the grid, not to 'now'
        for d in self.dates:
            if d.weekday() == idx:
                return d
    def summary(self, blocks):
        return " ".join(f"{b['date']:%a}{b['part'][:3]}:{'W' if b['wet'] else 'd'}{'S' if b['severe'] else ''}"
                        for b in blocks)

def weekday_hits(low, lang):
    out = []
    for i, pat in enumerate(WEEKDAYS[lang]):
        for m in re.finditer(r"\b" + pat + WD_SUFFIX[lang], low):
            out.append((i, m))
    return out

def day_refs(sent, lang, grid):
    low = sent.lower()
    # Each list is ordered early-first, and a matched phrase is consumed, so the system prompt's own
    # "early Saturday morning" (00:00-06:00) cannot also be read as the 06:00 morning.
    rest, parts = low, []
    for pat, p in DAYPART[lang]:
        if re.search(pat, rest):
            parts.append(p)
            rest = re.sub(pat, " ", rest)
    refs = []
    for i, m in weekday_hits(low, lang):
        part = None
        if lang == "de" and re.search(r"nacht\s+von\s+\w+\s+(auf|zum)\s+$", low[:m.start()]):
            continue                   # the "auf Sonntag" end of "Nacht von Samstag auf Sonntag"
        if lang == "de" and re.search(r"nacht\s+(zum|auf(\s+den)?)\s+$", low[:m.start()]):
            d = grid.weekday_date(i)   # "in der Nacht zum Sonntag" is Saturday night
            if d:
                refs.append([d - timedelta(days=1), "night"])
            continue
        if lang == "de":
            tail = m.group(0)[len(re.match(WEEKDAYS["de"][i], m.group(0)).group(0)):]
            if tail.startswith("s") and (tail[1:] in DE_TAIL or tail == "s"):
                tail = tail[1:]   # genitive "Samstags…"
            if tail not in DE_TAIL and tail.endswith("s") and tail[:-1] in DE_TAIL:
                tail = tail[:-1]  # adverbial "Samstagabends", "sonntagnachts"
            part = DE_TAIL.get(tail)
            if part == "morning" and re.search(r"früh\w*\s+(am\s+)?$", low[:m.start()]):
                part = "early"    # "am frühen Samstagmorgen" / "früh am Samstagmorgen" is 00:00-06:00
        d = grid.weekday_date(i)
        if d:
            refs.append([d, part])
    for pat, off, part in RELATIVE[lang]:
        if re.search(pat, low):
            refs.append([grid.today + timedelta(days=off), part])
    if len(refs) == 1 and refs[0][1] is None and len(parts) == 1:
        refs[0][1] = parts[0]
    exact = []
    for t in QTIME.findall(sent):
        exact += grid.at(utc(t))
    return refs, exact

def blocks_for(refs, exact, grid):
    out = list(exact)
    for date, part in refs:
        if part == "night":
            out += grid.by_date_part(date, "evening") + grid.by_date_part(date + timedelta(days=1), "early")
        elif part == "earlymorning":  # German "Samstagfrüh": 00:00-06:00 or the 06:00 morning
            out += grid.by_date_part(date, "early") + grid.by_date_part(date, "morning")
        else:
            out += grid.by_date_part(date, part)
    return out

def aggregate_dry(low, lang):
    """-> (kind, hedged) when an aggregate term and a dry word share a clause within 6 words."""
    for cl in clauses(low):
        d = re.search(DRY[lang], cl)
        if not d:
            continue
        for pat, kind in AGGREGATE[lang]:
            a = re.search(pat, cl)
            if a:
                lo, hi = sorted((a.start(), d.start()))
                if len(cl[lo:hi].split()) <= 6:
                    hedged = bool(re.search(HEDGE[lang] + r"$", cl[:d.start()]))
                    return kind, hedged, cl
    return None

def aggregate_period(kind, cl, low, lang, grid, refs):
    if kind == "weekend":
        days = [d for d in grid.dates if d.weekday() >= 5][:2]
    else:
        days = [d for d in grid.dates if d >= grid.today]
    m = re.search(UNTIL[lang] + r"(\w+)", cl)
    if m:
        for i, pat in enumerate(WEEKDAYS[lang]):
            if re.match(pat, m.group(m.lastindex)):
                end = grid.weekday_date(i)
                if end:
                    days = [d for d in days if d <= end]
    blocks = [b for b in grid.blocks if b["date"] in days]
    if re.search(REST[lang], low):
        named = blocks_for(refs, [], grid)
        if named:
            last = max(b["start"] for b in named)
            blocks = [b for b in blocks if b["start"] > last]
    return blocks

# Claim clauses for C1, C3 and C4: not split on ":" (it would cut {q:time:...} tokens and clock times).
# An exclusion (negation, "rain-free", the PRIOR forecast) counts only inside the clause making the claim.
CLAIM_CLAUSE = re.compile(r"\s[—–-]\s|[;,]")

def screen(sections, lang, grid, changes):
    hits = []
    has_severe = any(b["severe"] for b in grid.blocks)
    for sec, text in sections:
        for sent in sentences(text):
            low = lowered_prose(sent)
            refs, exact = day_refs(sent, lang, grid)
            blks = blocks_for(refs, exact, grid)
            # C1 - per clause, so an exclusion about one day cannot cancel a rain claim about another. A
            # clause naming no day falls back to the whole sentence's days.
            for cl in CLAIM_CLAUSE.split(sent):
                cl_low = lowered_prose(cl)
                if not re.search(PRECIP[lang], cl_low) or re.search(PRECIP_FREE[lang], cl_low) \
                        or re.search(NEG[lang], cl_low) or re.search(PRIOR[lang], cl_low):
                    continue
                crefs, cexact = day_refs(cl, lang, grid)
                cblks = blocks_for(crefs, cexact, grid) if (crefs or cexact) else blks
                if cblks and not any(b["wet"] for b in cblks):
                    hits.append(("C1", sec, sent, grid.summary(cblks)))
                    break
            # C2 / C2h - PRIOR counts only in the clause carrying the aggregate claim
            ad = aggregate_dry(low, lang)
            if ad and not re.search(PRIOR[lang], ad[2]):
                kind, hedged, cl = ad
                pb = aggregate_period(kind, cl, low, lang, grid, refs)
                if any(b["wet"] for b in pb):
                    hits.append(("C2h" if hedged else "C2", sec, sent, grid.summary(pb)))
            # C3 / C3p
            storm_cls = [c for c in map(lowered_prose, CLAIM_CLAUSE.split(sent)) if re.search(STORM[lang], c)]
            if storm_cls:  # C3p only when EVERY storm clause is about the prior forecast
                cls = "C3p" if all(re.search(PRIOR[lang], c) for c in storm_cls) else "C3"
                if not has_severe:
                    hits.append((cls, sec, sent, "no severeFlag anywhere in grid"))
                elif blks and not any(b["severe"] for b in blks):
                    hits.append((cls, sec, sent, grid.summary(blks)))
            # C4 - against the COMPUTED change windows, not the grid
            if any(re.search(PRECIP[lang], c) and not re.search(PRIOR[lang], c)
                   for c in map(lowered_prose, CLAIM_CLAUSE.split(sent))):
                named = {d for d, _ in refs} | {b["date"] for b in exact}
                for s, e in changes:
                    wb = grid.in_window(s, e)
                    wd = sorted({b["date"] for b in wb})
                    if len(wd) >= 2 and wd[-1] in named and wd[0] not in named:
                        hits.append(("C4", sec, sent, grid.summary(wb)))
                        break
    return hits

EXPOSURE = ["nonsevere_storm", "early_only_rain", "midnight_wet_window", "partly_wet_weekend"]

def exposure(grid):
    """Which tempting conditions this grid has. A class cannot fail on a report that never tempts it."""
    by = {(b["date"], b["part"]): b for b in grid.blocks}
    found = set()
    if any(b["phen"] == "thunderstorm" and not b["severe"] for b in grid.blocks):
        found.add("nonsevere_storm")
    for d in grid.dates:
        early, morning = by.get((d, "early")), by.get((d, "morning"))
        if early and morning and early["wet"] and not morning["wet"]:
            found.add("early_only_rain")
        evening, next_early = by.get((d, "evening")), by.get((d + timedelta(days=1), "early"))
        if evening and next_early and evening["wet"] and next_early["wet"]:
            found.add("midnight_wet_window")
    weekend = [b for b in grid.blocks if b["date"].weekday() >= 5]
    if len({b["date"] for b in weekend}) == 2 and any(b["wet"] for b in weekend) \
            and sum(not b["wet"] for b in weekend) >= 5:
        found.add("partly_wet_weekend")
    return found

def run(rows):
    snap_tz = {r["ForecastSnapshotId"]: r["LocalityTz"] for r in rows if r.get("LocalityTz")}
    seen, cands, skipped, expo, units = {}, [], {}, {}, []
    for r in rows:
        if not r.get("StructuredReport"):
            skipped["no report"] = skipped.get("no report", 0) + 1
            continue
        sr = json.loads(r["StructuredReport"])
        lang = r.get("IsoCode") or r["RecipientId"].rsplit("_", 1)[-1]
        narr = sr.get("narrative", {}).get(lang)
        if narr is None or lang not in WEEKDAYS:
            skipped[f"lang {lang}"] = skipped.get(f"lang {lang}", 0) + 1
            continue
        tzname = r.get("LocalityTz") or snap_tz.get(r["ForecastSnapshotId"]) or r.get("RecipientTz")
        if not tzname:
            raise SystemExit(f"row {r['Id']}: no locality, sibling or recipient timezone - refusing UTC")
        sections = [(k, narr.get(k)) for k in ("changeSummary", "closing") if narr.get(k)]
        text = "\n".join(t for _, t in sections)
        key = (r["ForecastSnapshotId"], lang, hashlib.sha1(text.encode()).hexdigest())
        if key in seen:
            seen[key].append(r["Id"]); continue
        seen[key] = [r["Id"]]
        units.append(dict(ids=seen[key], lang=lang, snap=r["ForecastSnapshotId"], at=r["SentAtUtc"], text=text))
        grid = Grid(r["SnapshotBody"], tz_of(tzname), utc(r["SentAtUtc"]))
        expo.setdefault(r["ForecastSnapshotId"], exposure(grid))
        changes = [(utc(c["window"]["startUtc"]), utc(c["window"]["endUtc"])) for c in sr.get("changes", [])]
        for cls, sec, sent, why in screen(sections, lang, grid, changes):
            cands.append(dict(cls=cls, lang=lang, sec=sec, ids=seen[key], sent=sent, grid=why,
                              at=r["SentAtUtc"], snap=r["ForecastSnapshotId"]))
    return seen, cands, skipped, expo, units

def sample(units, cands, per_lang=10, seed=504):
    """Up to per_lang reports per language that raised NO candidate, chosen reproducibly."""
    flagged = {c["ids"][0] for c in cands}
    bylang = {}
    for u in sorted(units, key=lambda u: u["ids"][0]):
        if u["ids"][0] not in flagged:
            bylang.setdefault(u["lang"], []).append(u)
    rng = random.Random(seed)
    return {lang: rng.sample(us, min(per_lang, len(us))) for lang, us in sorted(bylang.items())}

# ---------------------------------- selftest fixture ----------------------------------
# Blocks are LOCAL-aligned, as production's are: America/Chicago (CDT, UTC-5) 00:00 local = 05Z.
BASE = datetime.fromisoformat("2026-09-11T05:00:00+00:00")   # Fri 11 Sep 00:00 local

def L(day, hour):  # local Fri=0 .. Mon=3, local hour -> UTC string
    return (BASE + timedelta(days=day, hours=hour)).strftime("%Y-%m-%dT%H:%M:%SZ")

SAT_EVE, SUN_EARLY, SUN_AM, SUN_PM, SUN_EVE, MON_EARLY = L(1, 18), L(2, 0), L(2, 6), L(2, 12), L(2, 18), L(3, 0)

def _grid(wet=(), sev=(), storm=()):
    """wet = rain; sev = a severe thunderstorm; storm = a NON-severe thunderstorm."""
    blocks = []
    for i in range(24):
        s = (BASE + timedelta(hours=6 * i)).strftime("%Y-%m-%dT%H:%M:%SZ")
        w = s in wet or s in sev or s in storm
        b = {"startUtc": s, "skyState": "overcast", "obscuration": "none",
             "temperatureCelsius": {"min": 20, "max": 25}, "windKt": {"min": 5, "max": 10},
             "precipExpectation": "likely" if w else "none", "severeFlag": s in sev}
        if w:
            b["precipPhenomenon"] = "thunderstorm" if (s in sev or s in storm) else "rain"
        blocks.append(b)
    return json.dumps({"schemaVersion": 3, "blocks": blocks})

X = lambda s, e: {"window": {"startUtc": s, "endUtc": e}}
FIX = [  # (id, lang, text, wet, severe, changes, expected)
    (1, "en", "Rain arrives Saturday evening.",                           (SUN_AM,), (), [], {"C1"}),
    (2, "en", "Rain arrives Sunday morning.",                             (SUN_AM,), (), [], set()),
    (3, "en", "The weekend stays dry.",                                   (SUN_AM,), (), [], {"C2"}),
    (4, "en", "The weekend stays dry.",                                   (), (), [], set()),
    (5, "en", "Storms are possible Sunday afternoon.",                    (SUN_PM,), (), [], {"C3"}),
    (6, "en", "Severe storms are possible Sunday afternoon.",             (), (SUN_PM,), [], set()),
    (7, "en", "Rain is now possible in the early hours of Monday.",       (SUN_EVE, MON_EARLY), (),
     [X(SUN_EVE, L(3, 6))], {"C4"}),
    (8, "en", "Rain is now possible Sunday evening into the early hours of Monday.", (SUN_EVE, MON_EARLY), (),
     [X(SUN_EVE, L(3, 6))], set()),
    (9, "de", "Das Wochenende bleibt trocken.",                           (SUN_AM,), (), [], {"C2"}),
    (10, "de", "Am Samstagabend setzt Regen ein.",                        (SUN_AM,), (), [], {"C1"}),
    (11, "es", "Se esperan tormentas el domingo por la tarde.",           (SUN_PM,), (), [], {"C3"}),
    (12, "eo", "La semajnfino restas seka.",                              (SUN_AM,), (), [], {"C2"}),
    (13, "en", "Rain ends by Saturday evening.",                          (SUN_AM,), (), [], set()),
    (14, "en", "Rain arrives {q:time:" + SAT_EVE + "}.",                  (SUN_AM,), (), [], {"C1"}),
    (15, "de", "Am Sonntagnachmittag sind Gewitter möglich.",             (SUN_PM,), (), [], {"C3"}),
    (16, "eo", "Pluvo alvenas sabate vespere.",                           (SUN_AM,), (), [], {"C1"}),
    (17, "en", "The rain chance for Saturday evening has been removed.",  (SUN_AM,), (), [], set()),
    (18, "en", "Sunday stays dry — a hot start to the week.",             (MON_EARLY,), (), [], set()),
    (19, "en", "The week stays dry through Saturday.",                    (SUN_AM,), (), [], set()),
    (20, "en", "The week stays dry through Sunday.",                      (SUN_AM,), (), [], {"C2"}),
    (21, "en", "The weekend stays mostly dry.",                           (SUN_AM,), (), [], {"C2h"}),
    (22, "en", "The storm chance for Sunday afternoon has been dropped.", (), (), [], {"C3p"}),
    (23, "en", "Saturday clears out through the morning, then stays dry through the rest of the weekend.",
     (L(1, 0),), (), [], set()),
    (24, "en", "Saturday stays dry through the rest of the weekend.",     (SUN_PM,), (), [], {"C2"}),
    # proximity: dry and "week" in ONE clause but far apart - not a claim about the week
    (25, "en", "Sunday stays clear and dry with highs near 30 degrees and light winds to start the week.",
     (MON_EARLY,), (), [], set()),
    # anchoring: the report's own day (Friday) must resolve to the grid's first date, not next week
    (26, "en", "Rain arrives Friday evening.",                            (SUN_AM,), (), [], {"C1"}),
    # the storm gate's "write around it" phrasing: no storm word, still C3
    (27, "en", "The Saturday evening block now carries a convective signal.", (SAT_EVE,), (), [], {"C3"}),
    # the system prompt's own name for 00:00-06:00; the rain IS in Saturday's early hours, so no C1
    (28, "en", "Rain is possible early Saturday morning.",               (L(1, 0),), (), [], set()),
    # "Samstagnacht" / "sabate nokte" is the night FOLLOWING Saturday: rain only in Saturday's own early
    # hours is a wrong-day claim; rain on Saturday evening is not
    (29, "de", "Regen ist Samstagnacht möglich.",                        (L(1, 0),), (), [], {"C1"}),
    (30, "de", "Regen ist Samstagnacht möglich.",                        (SAT_EVE,), (), [], set()),
    (31, "eo", "Pluvo eblas sabate nokte.",                              (L(1, 0),), (), [], {"C1"}),
    # the system prompt's early-morning wording, per language: rain IS in Saturday's early hours
    (32, "de", "Regen ist am frühen Samstagmorgen möglich.",             (L(1, 0),), (), [], set()),
    (33, "es", "Lluvia posible el sábado temprano por la mañana.",       (L(1, 0),), (), [], set()),
    (34, "eo", "Pluvo eblas sabate frumatene.",                          (L(1, 0),), (), [], set()),
    (35, "en", "Rain is possible early on Saturday morning.",            (L(1, 0),), (), [], set()),
    # German bare "morgen" is "tomorrow" (Saturday here), not the morning: rain Saturday afternoon
    (36, "de", "Morgen gibt es Regen.",                                  (L(1, 12),), (), [], set()),
    # a separate-word night is the night following the day too
    (37, "de", "Regen ist am Samstag in der Nacht möglich.",             (L(1, 0),), (), [], {"C1"}),
    # "in der Nacht zum Sonntag" is Saturday night: rain in Sunday's early hours falls inside it
    (38, "de", "Regen ist in der Nacht zum Sonntag möglich.",            (L(2, 0),), (), [], set()),
    # "am frühen Morgen" is the early hours, not "tomorrow": no day named, so nothing to contradict
    (39, "de", "Am frühen Morgen ist Regen möglich.",                    (L(0, 0),), (), [], set()),
    # ...and with a day named it is that day's 00:00-06:00, so rain only in the 06:00 morning is a C1
    (40, "de", "Heute ist am frühen Morgen Regen möglich.",              (L(0, 6),), (), [], {"C1"}),
    # "Saturday night" is the night FOLLOWING Saturday, so Saturday's own early hours are the wrong day
    (41, "en", "Rain is possible Saturday night.",                        (L(1, 0),), (), [], {"C1"}),
    (42, "en", "Rain is possible tomorrow night.",                        (L(1, 0),), (), [], {"C1"}),
    # "heute <part>" is today's part alone, and "heute Morgen" is this morning, not tomorrow
    (43, "de", "Heute Abend setzt Regen ein.",                            (L(0, 6),), (), [], {"C1"}),
    (44, "de", "Heute Morgen gibt es Regen.",                             (L(1, 6),), (), [], {"C1"}),
    (45, "de", "Regen ist in der Nacht von Samstag auf Sonntag möglich.", (L(2, 6),), (), [], {"C1"}),
    # adverbial compounds name the day too
    (46, "de", "Samstagabends setzt Regen ein.",                          (L(1, 6),), (), [], {"C1"}),
    (47, "de", "Samstagfrüh setzt Regen ein.",                            (L(1, 12),), (), [], {"C1"}),
    # two days in one sentence: each compound must keep its own day-part
    (50, "de", "Regen fällt Samstagabends und Sonntagmorgens.",           (L(1, 6),), (), [], {"C1"}),
    # C2 is "dry, clear or quiet" in every language
    (51, "en", "The weekend stays clear.",                                (SUN_AM,), (), [], {"C2"}),
    (52, "en", "The weekend stays quiet.",                                (), (), [], set()),
    (53, "es", "El fin de semana estará tranquilo.",                      (SUN_AM,), (), [], {"C2"}),
    (54, "de", "Das Wochenende bleibt ruhig.",                            (SUN_AM,), (), [], {"C2"}),
    (55, "eo", "La semajnfino restas trankvila.",                         (SUN_AM,), (), [], {"C2"}),
    # a dry word about ANOTHER day does not excuse rain placed in a dry block (only Monday morning is wet)
    (56, "en", "Rain arrives Saturday evening; Sunday stays quiet.",      (L(3, 6),), (), [], {"C1"}),
    (57, "en", "Rain arrives Saturday evening; Sunday stays dry.",        (L(3, 6),), (), [], {"C1"}),
    # ...but a precipitation word negated in the phrase itself is not a rain claim
    (58, "en", "Saturday evening stays rain-free.",                       (L(3, 6),), (), [], set()),
    # an exclusion in ONE clause (rain-free / no / removed) must not cancel a rain claim in another
    (59, "en", "Rain arrives Saturday evening; Sunday stays rain-free.",  (L(3, 6),), (), [], {"C1"}),
    (60, "en", "Rain arrives Saturday evening; no rain Sunday.",          (L(3, 6),), (), [], {"C1"}),
    (61, "en", "Rain arrives Saturday evening, with the earlier chance for Sunday removed.", (L(3, 6),), (), [], {"C1"}),
    (62, "de", "Samstagabend Regen, Sonntag regenfrei.",                  (L(3, 6),), (), [], {"C1"}),
    (63, "en", "Rain arrives Saturday evening; Sunday stays rain-free.",  (SAT_EVE,), (), [], set()),
    (64, "en", "Rain arrives Saturday evening; no rain Sunday.",          (SAT_EVE,), (), [], set()),
    # the rain clause's OWN days decide: a wet Sunday named in another clause does not excuse Saturday
    (65, "en", "Rain arrives Saturday evening; Sunday stays mostly cloudy.", (SUN_AM,), (), [], {"C1"}),
    # a rain clause naming no day falls back to the sentence's day and day-part
    (66, "en", "Saturday, rain arrives in the evening.",                  (SUN_AM,), (), [], {"C1"}),
    # a PRIOR clause about another day must not suppress C2 / C4 or turn a current storm claim into C3p
    (67, "en", "Storms are possible Sunday afternoon; the earlier rain chance for Saturday was removed.",
     (SUN_PM,), (), [], {"C3"}),
    (68, "en", "The weekend stays dry; the earlier chance for Friday was removed.", (SUN_AM,), (), [], {"C2"}),
    (69, "en", "Rain is now possible in the early hours of Monday; the Saturday chance was removed.",
     (SUN_EVE, MON_EARLY), (), [X(SUN_EVE, L(3, 6))], {"C4"}),
    # ...while PRIOR in the claim's OWN clause still excludes it
    # two storm clauses, one current and one about the prior forecast: the current one makes it C3
    (72, "en", "Storms are possible Sunday afternoon; the earlier storm chance for Saturday was dropped.",
     (SUN_PM,), (), [], {"C3"}),
    (70, "en", "The earlier dry weekend forecast was dropped; rain arrives Sunday morning.", (SUN_AM,), (), [], set()),
    (71, "en", "Rain was expected in the early hours of Monday.",         (SUN_EVE, MON_EARLY), (),
     [X(SUN_EVE, L(3, 6))], set()),
    # the early hours in their natural word order: rain IS in Saturday's 00:00-06:00
    (48, "es", "Lluvia posible el sábado por la mañana temprano.",        (L(1, 0),), (), [], set()),
    (49, "eo", "Pluvo eblas sabate frue matene.",                         (L(1, 0),), (), [], set()),
]

def selftest():
    rows = []
    for fid, lang, text, wet, sev, ch, _ in FIX:
        narr = {"changeSummary": text if ch else None, "closing": "Quiet." if ch else text}
        rows.append(dict(Id=fid, ForecastSnapshotId=fid, IsoCode=lang, RecipientId=f"t_{lang}",
                         LocalityTz="America/Chicago", SentAtUtc=L(0, 9), SnapshotBody=_grid(wet, sev),
                         StructuredReport=json.dumps({"changes": ch, "narrative": {lang: narr}})))
    # a dropped recipient: no IsoCode / LocalityTz, sibling row supplies the timezone
    rows.append(dict(Id=99, ForecastSnapshotId=1, IsoCode=None, RecipientId="gone_de", LocalityTz=None,
                     SentAtUtc=L(0, 9), SnapshotBody=_grid((SUN_AM,)),
                     StructuredReport=json.dumps({"changes": [], "narrative": {
                         "de": {"changeSummary": None, "closing": "Am Samstagabend setzt Regen ein."}}})))
    # no locality timezone and no sibling row: the recipient's own timezone must be used
    rows.append(dict(Id=98, ForecastSnapshotId=98, IsoCode="en", RecipientId="nolocality_en", LocalityTz=None,
                     RecipientTz="America/Chicago", SentAtUtc=L(0, 9), SnapshotBody=_grid((SUN_AM,)),
                     StructuredReport=json.dumps({"changes": [], "narrative": {
                         "en": {"changeSummary": None, "closing": "Rain arrives Saturday evening."}}})))
    # DST fall-back (2026-11-01, Chicago): the early-hours block runs 05Z-12Z, seven hours. A {q:time} in its
    # seventh hour belongs to that dry block; with a fixed 6-hour block it would match nothing.
    fb = json.dumps({"schemaVersion": 3, "blocks": [
        {"startUtc": "2026-11-01T05:00:00Z", "precipExpectation": "none", "severeFlag": False},
        {"startUtc": "2026-11-01T12:00:00Z", "precipExpectation": "likely", "precipPhenomenon": "rain", "severeFlag": False},
        {"startUtc": "2026-11-01T18:00:00Z", "precipExpectation": "none", "severeFlag": False},
        {"startUtc": "2026-11-02T00:00:00Z", "precipExpectation": "none", "severeFlag": False}]})
    rows.append(dict(Id=97, ForecastSnapshotId=97, IsoCode="en", RecipientId="dst_en", LocalityTz="America/Chicago",
                     SentAtUtc="2026-11-01T04:00:00Z", SnapshotBody=fb,
                     StructuredReport=json.dumps({"changes": [], "narrative": {
                         "en": {"changeSummary": None, "closing": "Rain arrives {q:time:2026-11-01T11:30:00Z}."}}})))
    # the LAST block on a DST day: fall-back, the early block runs 05Z-12Z, so 11:30Z is inside it (dry)
    fb_last = json.dumps({"schemaVersion": 3, "blocks": [
        {"startUtc": "2026-10-31T23:00:00Z", "precipExpectation": "likely", "precipPhenomenon": "rain", "severeFlag": False},
        {"startUtc": "2026-11-01T05:00:00Z", "precipExpectation": "none", "severeFlag": False}]})
    rows.append(dict(Id=96, ForecastSnapshotId=96, IsoCode="en", RecipientId="dstlast_en", LocalityTz="America/Chicago",
                     SentAtUtc="2026-10-31T20:00:00Z", SnapshotBody=fb_last,
                     StructuredReport=json.dumps({"changes": [], "narrative": {
                         "en": {"changeSummary": None, "closing": "Rain arrives {q:time:2026-11-01T11:30:00Z}."}}})))
    # spring-forward, the early block runs 06Z-11Z, so 11:30Z is PAST the last (dry) block: nothing to contradict
    sf_last = json.dumps({"schemaVersion": 3, "blocks": [
        {"startUtc": "2026-03-08T00:00:00Z", "precipExpectation": "likely", "precipPhenomenon": "rain", "severeFlag": False},
        {"startUtc": "2026-03-08T06:00:00Z", "precipExpectation": "none", "severeFlag": False}]})
    rows.append(dict(Id=95, ForecastSnapshotId=95, IsoCode="en", RecipientId="sflast_en", LocalityTz="America/Chicago",
                     SentAtUtc="2026-03-07T20:00:00Z", SnapshotBody=sf_last,
                     StructuredReport=json.dumps({"changes": [], "narrative": {
                         "en": {"changeSummary": None, "closing": "Rain arrives {q:time:2026-03-08T11:30:00Z}."}}})))
    _, cands, skipped, _, units = run(rows)
    got = {}
    for c in cands:
        got.setdefault(c["ids"][0], set()).add(c["cls"])
    bad = 0
    EXTRA = [(99, "de", "(dropped recipient, tz from sibling)", 0, 0, 0, {"C1"}),
             (98, "en", "(no locality tz, no sibling: the recipient's own tz)", 0, 0, 0, {"C1"}),
             (97, "en", "(DST fall-back: a {q:time} in the 7-hour early block)", 0, 0, 0, {"C1"}),
             (96, "en", "(DST fall-back, LAST block: its 7th hour is still inside it)", 0, 0, 0, {"C1"}),
             (95, "en", "(DST spring-forward, LAST block: its 6th hour is past it)", 0, 0, 0, set())]
    for fid, lang, text, _, _, _, exp in FIX + EXTRA:
        g = got.get(fid, set())
        ok = g == exp
        bad += not ok
        print(f"{'ok  ' if ok else 'FAIL'} #{fid:<2} {lang} expect={sorted(exp) or '-'} got={sorted(g) or '-'}  {text}")
    if skipped:
        bad += 1; print("FAIL skipped rows:", skipped)
    tzc = tz_of("America/Chicago")
    def expo_of(**kw):
        return exposure(Grid(_grid(**kw), tzc, utc(L(0, 9))))
    EXPO_CHECKS = [  # (condition, planted grid, clean twin differing only in the condition's defining part)
        ("nonsevere_storm", dict(storm=(SUN_PM,)), dict(sev=(SUN_PM,))),
        ("early_only_rain", dict(wet=(SUN_EARLY,)), dict(wet=(SUN_EARLY, SUN_AM))),
        ("midnight_wet_window", dict(wet=(SUN_EVE, MON_EARLY)), dict(wet=(SUN_EVE,))),
        ("partly_wet_weekend", dict(wet=(SUN_AM,)), dict()),
    ]
    for cond, planted_kw, clean_kw in EXPO_CHECKS:
        ok = cond in expo_of(**planted_kw) and cond not in expo_of(**clean_kw)
        bad += not ok
        print(f"{'ok  ' if ok else 'FAIL'} exposure {cond}: found where planted, absent in its clean twin")
    # the hand-read sample: every unflagged report is eligible, and no flagged one ever is
    flagged = {c["ids"][0] for c in cands}
    picked = [u["ids"][0] for us in sample(units, cands, per_lang=len(units)).values() for u in us]
    unflagged = {u["ids"][0] for u in units} - flagged
    ok = bool(unflagged) and bool(flagged) and set(picked) == unflagged and len(picked) == len(unflagged)
    bad += not ok
    print(f"{'ok  ' if ok else 'FAIL'} sample: all {len(unflagged)} unflagged reports eligible, none of {len(flagged)} flagged")
    n = len(FIX) + len(EXTRA) + len(EXPO_CHECKS) + 1
    planted = sum(1 for f in FIX if f[6]) + len(EXTRA) + len(EXPO_CHECKS) + 1
    print(f"\n{n - bad}/{n} as expected  ({planted} planted, {n - planted} clean twins)")
    return 0 if bad == 0 else 1

if __name__ == "__main__":
    if sys.argv[1:] == ["--selftest"]:
        sys.exit(selftest())
    rows = [json.loads(l) for l in open(sys.argv[1], encoding="utf-8") if l.strip()]
    seen, cands, skipped, expo, units = run(rows)
    print(f"rows={len(rows)}  unique (snapshot,lang,prose)={len(seen)}  skipped={skipped}")
    print("exposure (distinct snapshots with the condition): "
          + "  ".join(f"{k}={sum(k in f for f in expo.values())}" for k in EXPOSURE))
    bycls = {}
    for c in cands:
        bycls.setdefault((c["cls"], c["lang"]), []).append(c)
    for k in sorted(bycls):
        print(f"{k[0]:4} {k[1]}: {len(bycls[k])}")
    json.dump(cands, open(sys.argv[1] + ".candidates.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    picks = sample(units, cands)
    print("sample (unflagged, for the hand-read): " + "  ".join(f"{k}={len(v)}" for k, v in picks.items()))
    json.dump(picks, open(sys.argv[1] + ".sample.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
