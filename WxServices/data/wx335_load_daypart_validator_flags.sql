-- WX-335: load the DayPart validator-safe flags (ValidatorUse = 'Yes') for each enabled language.
-- Run against the WeatherData database (sqlcmd -E -C, or SSMS). Safe to run more than once
-- (idempotent: it re-sets the same flags and never creates rows).
--
-- Background. After the WX-331 pivot the ONLY surviving deterministic prose check is the
-- {q:time}<->day-part agreement validator, which keys on a language's DayPart1-4 words -- but only
-- those a native marks UNAMBIGUOUS (ValidatorUse = 'Yes'). An EF migration can reach only en
-- (LanguageId 37 is the sole migration-seeded language; es/de/eo/da DayPart rows are generated at
-- runtime by the WX-250 top-up), so the flags load here instead -- uniformly, per enabled language,
-- with no privilege for English. The WX-250 top-up is fill-only / never-clobber (ReportWorker
-- ApplyTopUpAsync), so these flags are durable once set.
--
-- Validator-safe DayPart tokens per language (every other token stays 'No', the column default):
--   en: DayPart1-4 (early hours / morning / afternoon / evening) -- all unambiguous.
--   es: DayPart1 only (madrugada); DayPart2-4 (manana / tarde / noche) are ambiguous -> stay No,
--       prompt-governed.
--   de/eo/da: none yet -- vetted natively later (WX-338 for de).

-- en: all four DayPart tokens are validator-safe.
UPDATE lt
   SET lt.ValidatorUse = 'Yes'
  FROM LanguageTemplates lt
  JOIN Languages l ON l.Id = lt.LanguageId
 WHERE l.IsoCode = 'en'
   AND lt.Token IN ('DayPart1', 'DayPart2', 'DayPart3', 'DayPart4')
   AND lt.ValidatorUse <> 'Yes';
PRINT CONCAT('en DayPart1-4 flagged Yes: ', @@ROWCOUNT, ' row(s) updated.');
GO

-- es: only the pre-dawn word (DayPart1 = madrugada) is unambiguous.
UPDATE lt
   SET lt.ValidatorUse = 'Yes'
  FROM LanguageTemplates lt
  JOIN Languages l ON l.Id = lt.LanguageId
 WHERE l.IsoCode = 'es'
   AND lt.Token = 'DayPart1'
   AND lt.ValidatorUse <> 'Yes';
PRINT CONCAT('es DayPart1 flagged Yes: ', @@ROWCOUNT, ' row(s) updated.');
GO
