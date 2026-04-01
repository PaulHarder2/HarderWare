-- Migration 002: WxVis additions
-- Run against the WeatherData database on .\SQLEXPRESS.
-- Safe to run more than once — each statement checks existence before acting.

-- ── 1. WxStations table ───────────────────────────────────────────────────────
-- Holds geographic metadata for every METAR reporting station encountered.
-- Populated opportunistically by WxParser.Svc after each METAR fetch cycle.

IF NOT EXISTS (
    SELECT 1 FROM sys.tables WHERE name = 'WxStations'
)
BEGIN
    CREATE TABLE WxStations (
        IcaoId      char(4)        NOT NULL,
        Name        nvarchar(100)  NULL,
        Lat         float          NULL,
        Lon         float          NULL,
        ElevationFt float          NULL,
        CONSTRAINT PK_WxStations PRIMARY KEY (IcaoId)
    );
    PRINT 'Created table WxStations.';
END
ELSE
BEGIN
    PRINT 'Table WxStations already exists — skipped.';
END
GO

-- ── 1a. WxStations.Lat / Lon — allow NULL (stations unresolvable via AWC API) ──
-- Needed if the table was already created with NOT NULL on these columns.

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('WxStations') AND name = 'Lat' AND is_nullable = 0
)
BEGIN
    ALTER TABLE WxStations ALTER COLUMN Lat float NULL;
    PRINT 'Altered WxStations.Lat to allow NULL.';
END
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('WxStations') AND name = 'Lon' AND is_nullable = 0
)
BEGIN
    ALTER TABLE WxStations ALTER COLUMN Lon float NULL;
    PRINT 'Altered WxStations.Lon to allow NULL.';
END
GO

-- ── 2. PrMslPa column on GfsGrid ─────────────────────────────────────────────
-- Mean sea-level pressure in Pascals from the GFS PRMSL field.
-- Divide by 100 to convert to hPa / mb.

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('GfsGrid') AND name = 'PrMslPa'
)
BEGIN
    ALTER TABLE GfsGrid ADD PrMslPa real NULL;
    PRINT 'Added column GfsGrid.PrMslPa.';
END
ELSE
BEGIN
    PRINT 'Column GfsGrid.PrMslPa already exists — skipped.';
END
GO
