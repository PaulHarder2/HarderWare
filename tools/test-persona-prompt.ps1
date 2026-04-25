# test-persona-prompt.ps1
#
# Phase 2 acceptance test for WX-50: verifies that AboutPaul.md (the cached
# author-persona prefix) actually shapes Claude's output, including across
# languages, by sending a single Anthropic Messages API call whose system
# prefix matches production exactly.
#
# Reads:
#   - AboutPaul.md (repo root)
#   - WxServices/appsettings.shared.json -> ConnectionStrings.WeatherData
#   - WxServices/appsettings.shared.json -> Claude.Model
#   - GlobalSettings.ClaudeApiKey from the WeatherData database (Id = 1)
#
# All three are pulled from the same sources WxReport.Svc uses, so this
# script tests against the same model/key/persona the live service does.
#
# Run from any directory; paths resolve relative to the repo root.

$ErrorActionPreference = "Stop"

# ---- locate inputs ---------------------------------------------------------

$repoRoot        = Split-Path -Parent $PSScriptRoot
$personaPath     = Join-Path $repoRoot "AboutPaul.md"
$appSettingsPath = Join-Path $repoRoot "WxServices\appsettings.shared.json"

if (-not (Test-Path $personaPath))     { throw "AboutPaul.md not found at $personaPath" }
if (-not (Test-Path $appSettingsPath)) { throw "appsettings.shared.json not found at $appSettingsPath" }

$persona     = Get-Content $personaPath     -Raw
$appSettings = Get-Content $appSettingsPath -Raw | ConvertFrom-Json

$connStr = $appSettings.ConnectionStrings.WeatherData
$model   = $appSettings.Claude.Model
if (-not $connStr) { throw "ConnectionStrings.WeatherData is empty in appsettings.shared.json" }
if (-not $model)   { throw "Claude.Model is empty in appsettings.shared.json" }

# ---- pull API key from GlobalSettings (Id = 1) -----------------------------

$sql = New-Object System.Data.SqlClient.SqlConnection $connStr
$sql.Open()
try {
    $cmd = $sql.CreateCommand()
    $cmd.CommandText = "SELECT ClaudeApiKey FROM GlobalSettings WHERE Id = 1"
    $apiKey = $cmd.ExecuteScalar()
}
finally {
    $sql.Close()
    $sql.Dispose()
}

if (-not $apiKey -or [string]::IsNullOrWhiteSpace([string]$apiKey)) {
    throw "ClaudeApiKey is empty in GlobalSettings (Id = 1)."
}

# ---- the test prompt -------------------------------------------------------

$prompt = @'
An unexpected severe storm watch has been added to the forecast. Write a short, non-rhyming poem of the sort that might appear in the report. The poem's twist (final line or turn) should prompt the reader to consider practical consequences rather than amuse - for example, a line like "Do you know where your spare batteries are?" Provide three versions: in English, Spanish, and Esperanto.
'@

# ---- request ---------------------------------------------------------------

# Mirrors the production system-block layout in ClaudeClient.cs: persona
# first with cache_control: ephemeral, dynamic content (here, just the
# user prompt) second.
$body = @{
    model      = $model
    max_tokens = 4096
    system     = @(
        @{
            type          = "text"
            text          = $persona
            cache_control = @{ type = "ephemeral" }
        }
    )
    messages   = @(
        @{ role = "user"; content = $prompt }
    )
} | ConvertTo-Json -Depth 10

Write-Host "Calling $model with persona prefix ($($persona.Length) chars)..."

$resp = Invoke-RestMethod `
    -Uri     "https://api.anthropic.com/v1/messages" `
    -Method  POST `
    -Body    $body `
    -Headers @{
        "x-api-key"          = $apiKey
        "anthropic-version"  = "2023-06-01"
        "Content-Type"       = "application/json"
    }

# ---- output ----------------------------------------------------------------

Write-Host ""
Write-Host "----- response -----"
Write-Output $resp.content[0].text
Write-Host ""
Write-Host "----- usage -----"
$resp.usage | Format-List
