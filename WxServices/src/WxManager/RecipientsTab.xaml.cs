using MetarParser.Data;
using MetarParser.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WxManager;

// ── UI data types ─────────────────────────────────────────────────────────────

/// <summary>Lightweight projection of a <see cref="Recipient"/> row used to populate the left-pane ListBox.</summary>
internal sealed class RecipientListItem
{
    /// <summary>Surrogate primary key from the <c>Recipients</c> table.</summary>
    public int    DbId        { get; set; }

    /// <summary>Application-level recipient identifier (e.g. <c>"john-en"</c>).</summary>
    public string RecipientId { get; set; } = "";

    /// <summary>Display name of the recipient.</summary>
    public string Name        { get; set; } = "";

    /// <summary>Returns a human-readable string shown in the ListBox.</summary>
    /// <returns>A string in the form <c>"RecipientId — Name"</c>.</returns>
    public override string ToString() => $"{RecipientId} — {Name}";
}

/// <summary>One row in the Nearby Stations DataGrid, representing a single METAR station candidate.</summary>
internal sealed class StationRow
{
    /// <summary>ICAO station identifier.</summary>
    public string Icao         { get; set; } = "";

    /// <summary>Human-readable station name from <c>WxStations</c>, or empty if not found.</summary>
    public string Name         { get; set; } = "";

    /// <summary>Great-circle distance from the geocoded address in kilometres.</summary>
    public double DistKm       { get; set; }

    /// <summary>Formatted distance string for display (e.g. <c>"12.3"</c>).</summary>
    public string DistKmStr    => $"{DistKm:F1}";

    /// <summary>Number of METAR records in the local database for this station.</summary>
    public int    MetarCount   { get; set; }

    /// <summary>Formatted METAR count for display; shows <c>"—"</c> when zero.</summary>
    public string MetarCountStr => MetarCount == 0 ? "—" : MetarCount.ToString();

    /// <summary>Number of TAF records in the local database for this station.</summary>
    public int    TafCount     { get; set; }

    /// <summary>Formatted TAF count for display; shows <c>"—"</c> when zero.</summary>
    public string TafCountStr  => TafCount == 0 ? "—" : TafCount.ToString();
}

/// <summary>DTO matching the Aviation Weather Center METAR API JSON response.</summary>
internal sealed class AwcMetar
{
    /// <summary>ICAO station identifier from the AWC response.</summary>
    [JsonPropertyName("icaoId")] public string? IcaoId { get; set; }

    /// <summary>Station latitude from the AWC response.</summary>
    [JsonPropertyName("lat")]    public double? Lat    { get; set; }

    /// <summary>Station longitude from the AWC response.</summary>
    [JsonPropertyName("lon")]    public double? Lon    { get; set; }
}

// ── User control ──────────────────────────────────────────────────────────────

/// <summary>
/// Recipients tab content for WxManager.
/// <para>
/// Left pane: scrollable list of all recipients in the <c>Recipients</c> database
/// table, with a New button to begin adding a record.
/// </para>
/// <para>
/// Right pane: address lookup (Nominatim geocoding + AWC nearby-station query),
/// a collapsible Messages area for errors, a Nearby Stations DataGrid, and a
/// full set of recipient property fields.  Save writes to the database; Delete
/// removes the selected recipient after confirmation.
/// </para>
/// </summary>
public partial class RecipientsTab : UserControl
{
    // ── State ─────────────────────────────────────────────────────────────────

    private readonly DbContextOptions<WeatherDataContext> _dbOptions;
    private readonly HttpClient _http = new();

    /// <summary>
    /// Surrogate DB Id of the recipient currently loaded into the right pane,
    /// or <see langword="null"/> when creating a new recipient.
    /// </summary>
    private int? _currentRecipientDbId;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Initializes the control, wires unit ComboBoxes, and schedules the
    /// initial recipient list load once the control is fully rendered.
    /// </summary>
    /// <sideeffects>
    /// Reads <see cref="App.DbOptions"/> to obtain the database connection.
    /// Subscribes to <see cref="FrameworkElement.Loaded"/> to trigger an async DB query.
    /// </sideeffects>
    public RecipientsTab()
    {
        _dbOptions = App.DbOptions;
        InitializeComponent();

        TempUnitBox.ItemsSource     = new[] { "F", "C" };
        PressureUnitBox.ItemsSource = new[] { "inHg", "kPa" };
        WindUnitBox.ItemsSource     = new[] { "mph", "kph" };
        TzBox.ItemsSource = BuildIanaTimeZoneList();

        ClearRightPane();   // show defaults; buttons stay disabled (set in XAML)

        Loaded += async (_, _) => await LoadRecipientListAsync();
    }

    // ── Left pane: recipient list ─────────────────────────────────────────────

    /// <summary>
    /// Queries the <c>Recipients</c> table and rebinds the left-pane ListBox.
    /// Restores the selection to the currently edited recipient if it still exists.
    /// </summary>
    /// <returns>A <see cref="Task"/> that completes when the list is refreshed.</returns>
    /// <sideeffects>Executes an EF Core query; updates <c>RecipientList.ItemsSource</c> and selection on the UI thread.</sideeffects>
    private async Task LoadRecipientListAsync()
    {
        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);
            var items = await ctx.Recipients
                .OrderBy(r => r.RecipientId)
                .Select(r => new RecipientListItem { DbId = r.Id, RecipientId = r.RecipientId, Name = r.Name })
                .ToListAsync();

            var currentId = _currentRecipientDbId;

            // Temporarily detach handler so programmatic selection change doesn't reload fields.
            RecipientList.SelectionChanged -= RecipientList_SelectionChanged;
            RecipientList.ItemsSource = items;

            if (currentId.HasValue)
            {
                var item = items.FirstOrDefault(i => i.DbId == currentId.Value);
                if (item != null)
                    RecipientList.SelectedItem = item;
            }

            RecipientList.SelectionChanged += RecipientList_SelectionChanged;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load recipient list: {ex.Message}",
                "WxManager — Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Left pane: New button ─────────────────────────────────────────────────

    /// <summary>
    /// Clears the right pane and sets up a blank new-recipient state.
    /// </summary>
    /// <param name="sender">The New button.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>Clears all right-pane fields; deselects the ListBox without triggering <see cref="RecipientList_SelectionChanged"/>.</sideeffects>
    private void NewBtn_Click(object sender, RoutedEventArgs e)
    {
        _currentRecipientDbId = null;

        RecipientList.SelectionChanged -= RecipientList_SelectionChanged;
        RecipientList.SelectedItem = null;
        RecipientList.SelectionChanged += RecipientList_SelectionChanged;

        ClearRightPane();
        SaveBtn.IsEnabled   = true;
        CancelBtn.IsEnabled = true;
        DeleteBtn.IsEnabled = false;
    }

    // ── Left pane: selection ──────────────────────────────────────────────────

    /// <summary>
    /// Loads the selected recipient's data from the database into the right pane.
    /// </summary>
    /// <param name="sender">The recipient ListBox.</param>
    /// <param name="e">Event arguments containing the new selection.</param>
    /// <sideeffects>Executes an EF Core FindAsync; populates all right-pane fields via <see cref="LoadRecipientIntoFields"/>.</sideeffects>
    private async void RecipientList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecipientList.SelectedItem is not RecipientListItem item)
            return;

        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);
            var r = await ctx.Recipients.FindAsync(item.DbId);
            if (r == null) return;
            LoadRecipientIntoFields(r);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load recipient: {ex.Message}",
                "WxManager — Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Right pane: Address Lookup ────────────────────────────────────────────

    /// <summary>
    /// Geocodes the address in <c>AddressBox</c> via Nominatim, queries the Aviation
    /// Weather Center API for nearby active METAR stations, then queries the local
    /// database for observation and TAF counts per station.
    /// </summary>
    /// <param name="sender">The Look Up button.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>
    /// Makes HTTP requests to Nominatim and aviationweather.gov.
    /// Executes EF Core queries for METAR/TAF counts and station names.
    /// Updates <c>LatBox</c>, <c>LonBox</c>, <c>LocalityBox</c>, <c>MetarIcaoBox</c>,
    /// <c>StationsGrid</c>, <c>StationsGroup</c>, <c>BboxStatusText</c>,
    /// <c>MessagesBorder</c>, and <c>LookUpBtn</c>.
    /// </sideeffects>
    private async void LookUpBtn_Click(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        if (string.IsNullOrEmpty(address))
        {
            ShowMessage("Please enter an address before clicking Look Up.");
            return;
        }

        SetLookupInProgress(true);
        HideMessages();
        StationsGroup.Visibility   = Visibility.Collapsed;
        StationsGrid.ItemsSource   = null;
        BboxStatusText.Visibility  = Visibility.Collapsed;

        try
        {
            // ── Geocode ──────────────────────────────────────────────────────────

            var geo = await AddressGeocoder.LookupAsync(address, _http);
            if (geo is null)
            {
                ShowMessage("Address could not be geocoded — check spelling and try again.");
                return;
            }

            var (lat, lon, locality) = geo.Value;
            LatBox.Text      = lat.ToString("F7");
            LonBox.Text      = lon.ToString("F7");
            LocalityBox.Text = locality;

            // A successful geocode implicitly begins editing; enable Save/Cancel.
            SaveBtn.IsEnabled   = true;
            CancelBtn.IsEnabled = true;

            // ── Nearby METAR stations ─────────────────────────────────────────

            const double SearchRadius = 2.5;
            var bbox = $"{lat - SearchRadius},{lon - SearchRadius},{lat + SearchRadius},{lon + SearchRadius}";
            var url  = $"https://aviationweather.gov/api/data/metar?bbox={bbox}&hours=1&format=json";

            AwcMetar[]? awcResults;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", "WxManager/1.0");
                using var resp = await _http.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                awcResults = await resp.Content.ReadFromJsonAsync<AwcMetar[]>();
            }
            catch (Exception ex)
            {
                ShowMessage($"Aviation Weather API request failed: {ex.Message}");
                return;
            }

            var candidates = (awcResults ?? [])
                .Where(s => s.IcaoId is not null && s.Lat.HasValue && s.Lon.HasValue)
                .DistinctBy(s => s.IcaoId)
                .Select(s => (Station: s, DistKm: HaversineKm(lat, lon, s.Lat!.Value, s.Lon!.Value)))
                .OrderBy(x => x.DistKm)
                .Take(5)
                .ToList();

            if (candidates.Count == 0)
            {
                ShowMessage($"No active METAR stations found within {SearchRadius}° of the resolved coordinates. " +
                            "The address may be geocoded correctly — check the fetch bounding box.");
                return;
            }

            // ── DB lookups (batched) ──────────────────────────────────────────

            var icaos = candidates.Select(c => c.Station.IcaoId!).ToList();
            await using var ctx = new WeatherDataContext(_dbOptions);

            var metarCounts = await ctx.Metars
                .Where(m => icaos.Contains(m.StationIcao))
                .GroupBy(m => m.StationIcao)
                .Select(g => new { Icao = g.Key, Count = g.Count() })
                .ToListAsync();

            var tafCounts = await ctx.Tafs
                .Where(t => icaos.Contains(t.StationIcao))
                .GroupBy(t => t.StationIcao)
                .Select(g => new { Icao = g.Key, Count = g.Count() })
                .ToListAsync();

            var stationNames = await ctx.WxStations
                .Where(s => icaos.Contains(s.IcaoId))
                .Select(s => new { s.IcaoId, s.Name })
                .ToListAsync();

            var rows = candidates.Select(c =>
            {
                var icao = c.Station.IcaoId!;
                return new StationRow
                {
                    Icao       = icao,
                    Name       = stationNames.FirstOrDefault(x => x.IcaoId == icao)?.Name ?? "",
                    DistKm     = c.DistKm,
                    MetarCount = metarCounts.FirstOrDefault(x => x.Icao == icao)?.Count ?? 0,
                    TafCount   = tafCounts.FirstOrDefault(x => x.Icao == icao)?.Count ?? 0,
                };
            }).ToList();

            StationsGrid.ItemsSource = rows;
            StationsGroup.Visibility = Visibility.Visible;

            // Suggest MetarIcao only when the field is currently blank.
            if (string.IsNullOrEmpty(MetarIcaoBox.Text))
            {
                var bestRow = rows.FirstOrDefault(r => r.MetarCount > 0) ?? rows[0];
                MetarIcaoBox.Text = bestRow.Icao;
            }

            // ── Fetch bounding-box advisory ───────────────────────────────────

            if (App.FetchHomeLat.HasValue && App.FetchHomeLon.HasValue && App.FetchBoxDeg.HasValue)
            {
                var hl = App.FetchHomeLat.Value;
                var hlo = App.FetchHomeLon.Value;
                var bd  = App.FetchBoxDeg.Value;
                var inBox = lat >= hl  - bd && lat <= hl  + bd
                         && lon >= hlo - bd && lon <= hlo + bd;

                if (inBox)
                {
                    BboxStatusText.Text       = "Address is within the fetch bounding box.";
                    BboxStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0xDD, 0x88));
                }
                else
                {
                    BboxStatusText.Text       = "Address is outside the fetch bounding box — " +
                                               "nearby stations may not accumulate local observations.";
                    BboxStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x88));
                }
                BboxStatusText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            ShowMessage($"Unexpected error during lookup: {ex.Message}");
        }
        finally
        {
            SetLookupInProgress(false);
        }
    }

    // ── Right pane: Cancel (Messages area) ────────────────────────────────────

    /// <summary>
    /// Hides the Messages area and clears the address field so the user can
    /// enter a corrected address or abandon the lookup attempt.
    /// </summary>
    /// <param name="sender">The Cancel button inside the Messages border.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>Sets <c>MessagesBorder.Visibility</c> to Collapsed; clears <c>AddressBox</c>.</sideeffects>
    private void CancelLookupBtn_Click(object sender, RoutedEventArgs e)
    {
        HideMessages();
        AddressBox.Clear();
    }

    // ── Right pane: Station selection ─────────────────────────────────────────

    /// <summary>
    /// Updates <c>MetarIcaoBox</c> with the ICAO code of the station clicked
    /// in the Nearby Stations DataGrid.
    /// </summary>
    /// <param name="sender">The Nearby Stations DataGrid.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>Sets <c>MetarIcaoBox.Text</c>.</sideeffects>
    private void StationsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StationsGrid.SelectedItem is StationRow row)
            MetarIcaoBox.Text = row.Icao;
    }

    // ── Right pane: Save button ───────────────────────────────────────────────

    /// <summary>
    /// Validates the form fields then inserts a new row or updates the existing
    /// row in the <c>Recipients</c> table.
    /// </summary>
    /// <param name="sender">The Save button.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>
    /// Executes an EF Core INSERT or UPDATE; refreshes the recipient list via
    /// <see cref="LoadRecipientListAsync"/>; enables <c>DeleteBtn</c> after a
    /// successful insert.
    /// </sideeffects>
    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        var recipientId = RecipientIdBox.Text.Trim();
        var name        = NameBox.Text.Trim();
        var email       = EmailBox.Text.Trim();

        if (string.IsNullOrEmpty(recipientId)) { ShowMessage("Id is required.");    return; }
        if (string.IsNullOrEmpty(name))         { ShowMessage("Name is required.");  return; }
        if (string.IsNullOrEmpty(email))        { ShowMessage("Email is required."); return; }

        if (!Regex.IsMatch(recipientId, @"^[A-Za-z0-9_\-]+$"))
            { ShowMessage("Id may only contain letters, digits, hyphens, and underscores."); return; }

        try   { _ = new MailAddress(email); }
        catch { ShowMessage("Email address is not valid."); return; }

        var tzValue = TzBox.Text.Trim().Length > 0 ? TzBox.Text.Trim() : "UTC";
        if (!BuildIanaTimeZoneList().Contains(tzValue))
            { ShowMessage($"\"{tzValue}\" is not a recognized IANA timezone ID."); return; }

        var scheduledHoursRaw = ScheduledHoursBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(scheduledHoursRaw))
        {
            foreach (var token in scheduledHoursRaw.Split(','))
            {
                var t = token.Trim();
                if (!int.TryParse(t, out var h) || h < 0 || h > 23)
                    { ShowMessage($"Scheduled hours must be integers 0\u201323, comma-separated (got \"{t}\")."); return; }
            }
        }

        var metarRaw = MetarIcaoBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(metarRaw))
        {
            foreach (var token in metarRaw.Split(','))
            {
                if (!Regex.IsMatch(token.Trim(), @"^[A-Z0-9]{4}$", RegexOptions.IgnoreCase))
                    { ShowMessage($"METAR ICAO codes must be exactly 4 alphanumeric characters (got \"{token.Trim()}\")."); return; }
            }
        }

        var tafRaw = TafIcaoBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(tafRaw) && !tafRaw.Equals("NONE", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(tafRaw, @"^[A-Z0-9]{4}$", RegexOptions.IgnoreCase))
            { ShowMessage("TAF ICAO code must be exactly 4 alphanumeric characters, \"NONE\", or blank."); return; }

        var lat = double.TryParse(LatBox.Text, out var lv) ? lv : (double?)null;
        var lon = double.TryParse(LonBox.Text, out var lnv) ? lnv : (double?)null;
        if (lat is < -90 or > 90)   { ShowMessage("Latitude must be between \u221290 and +90."); return; }
        if (lon is < -180 or > 180) { ShowMessage("Longitude must be between \u2212180 and +180."); return; }

        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);

            // ── ICAO station validation ───────────────────────────────────────
            var bboxWarnings = new List<string>();

            if (!string.IsNullOrWhiteSpace(metarRaw))
            {
                foreach (var token in metarRaw.Split(','))
                {
                    var icao    = token.Trim().ToUpperInvariant();
                    var station = await ctx.WxStations.FirstOrDefaultAsync(s => s.IcaoId == icao);
                    if (station is null)
                        { ShowMessage($"METAR station \"{icao}\" was not found in the station database."); return; }
                    if (station.Lat is null || station.Lon is null)
                        { ShowMessage($"METAR station \"{icao}\" has no coordinates and cannot be used."); return; }
                    if (App.FetchHomeLat.HasValue && App.FetchHomeLon.HasValue && App.FetchBoxDeg.HasValue)
                    {
                        var bd = App.FetchBoxDeg.Value;
                        if (station.Lat < App.FetchHomeLat - bd || station.Lat > App.FetchHomeLat + bd ||
                            station.Lon < App.FetchHomeLon - bd || station.Lon > App.FetchHomeLon + bd)
                            bboxWarnings.Add($"{icao} is outside the fetch bounding box");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(tafRaw) && !tafRaw.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                var icao    = tafRaw.ToUpperInvariant();
                var station = await ctx.WxStations.FirstOrDefaultAsync(s => s.IcaoId == icao);
                if (station is null)
                    { ShowMessage($"TAF station \"{icao}\" was not found in the station database."); return; }
                if (station.Lat is null || station.Lon is null)
                    { ShowMessage($"TAF station \"{icao}\" has no coordinates and cannot be used."); return; }
                if (App.FetchHomeLat.HasValue && App.FetchHomeLon.HasValue && App.FetchBoxDeg.HasValue)
                {
                    var bd = App.FetchBoxDeg.Value;
                    if (station.Lat < App.FetchHomeLat - bd || station.Lat > App.FetchHomeLat + bd ||
                        station.Lon < App.FetchHomeLon - bd || station.Lon > App.FetchHomeLon + bd)
                        bboxWarnings.Add($"TAF station {icao} is outside the fetch bounding box");
                }
            }

            Recipient r;
            if (_currentRecipientDbId is null)
            {
                if (await ctx.Recipients.AnyAsync(x => x.RecipientId == recipientId))
                {
                    ShowMessage($"A recipient with Id \"{recipientId}\" already exists.");
                    return;
                }
                r = new Recipient();
                ctx.Recipients.Add(r);
            }
            else
            {
                r = await ctx.Recipients.FindAsync(_currentRecipientDbId.Value)
                    ?? throw new InvalidOperationException(
                        $"Recipient DB Id={_currentRecipientDbId} not found — it may have been deleted externally.");
            }

            r.RecipientId        = recipientId;
            r.Name               = name;
            r.Email              = email;
            r.Language           = NullIfEmpty(LanguageBox.Text);
            r.Timezone           = TzBox.Text.Trim().Length > 0 ? TzBox.Text.Trim() : "UTC";
            r.ScheduledSendHours = NullIfEmpty(ScheduledHoursBox.Text);
            r.Address            = NullIfEmpty(AddressBox.Text);
            r.LocalityName       = NullIfEmpty(LocalityBox.Text);
            r.Latitude           = lat;
            r.Longitude          = lon;
            r.MetarIcao          = NullIfEmpty(MetarIcaoBox.Text);
            r.TafIcao            = NullIfEmpty(TafIcaoBox.Text);
            r.TempUnit           = TempUnitBox.SelectedItem?.ToString() ?? "F";
            r.PressureUnit       = PressureUnitBox.SelectedItem?.ToString() ?? "inHg";
            r.WindSpeedUnit      = WindUnitBox.SelectedItem?.ToString() ?? "mph";

            await ctx.SaveChangesAsync();
            _currentRecipientDbId = r.Id;
            DeleteBtn.IsEnabled = true;
            if (bboxWarnings.Count > 0)
                ShowMessage($"Saved. Note: {string.Join("; ", bboxWarnings)} — data may not be fetched for these stations.");
            else
                ShowSuccessMessage("Saved successfully.");

            await LoadRecipientListAsync();
        }
        catch (Exception ex)
        {
            ShowMessage($"Save failed: {ex.Message}");
        }
    }

    // ── Right pane: Delete button ─────────────────────────────────────────────

    /// <summary>
    /// Prompts for confirmation then deletes the currently loaded recipient from
    /// the <c>Recipients</c> table.
    /// </summary>
    /// <param name="sender">The Delete button.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>
    /// Executes an EF Core DELETE after a MessageBox confirmation; clears the
    /// right pane and refreshes the recipient list via <see cref="LoadRecipientListAsync"/>.
    /// </sideeffects>
    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRecipientDbId is null) return;

        var displayName = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : RecipientIdBox.Text.Trim();
        var result = MessageBox.Show(
            $"Delete recipient \"{displayName}\"?\n\nThis cannot be undone.",
            "WxManager — Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);
            var r = await ctx.Recipients.FindAsync(_currentRecipientDbId.Value);
            if (r != null)
            {
                ctx.Recipients.Remove(r);
                await ctx.SaveChangesAsync();
            }

            _currentRecipientDbId = null;
            SetIdlePane();
            await LoadRecipientListAsync();
        }
        catch (Exception ex)
        {
            ShowMessage($"Delete failed: {ex.Message}");
        }
    }

    // ── Right pane: Cancel button ─────────────────────────────────────────────

    /// <summary>
    /// Discards any unsaved edits and returns the form to the idle (no-selection) state.
    /// </summary>
    /// <param name="sender">The Cancel button.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>Clears all right-pane fields; deselects the ListBox; disables Save, Cancel, and Delete.</sideeffects>
    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _currentRecipientDbId = null;

        RecipientList.SelectionChanged -= RecipientList_SelectionChanged;
        RecipientList.SelectedItem = null;
        RecipientList.SelectionChanged += RecipientList_SelectionChanged;

        SetIdlePane();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Populates all right-pane fields from the given database <see cref="Recipient"/> entity.
    /// </summary>
    /// <param name="r">The recipient entity whose data should fill the form.</param>
    /// <sideeffects>
    /// Sets text of all field TextBoxes and SelectedItem on all unit ComboBoxes.
    /// Hides the Messages border and Nearby Stations group.
    /// Enables <c>DeleteBtn</c>.
    /// </sideeffects>
    private void LoadRecipientIntoFields(Recipient r)
    {
        _currentRecipientDbId = r.Id;

        RecipientIdBox.Text      = r.RecipientId;
        NameBox.Text             = r.Name;
        EmailBox.Text            = r.Email;
        LanguageBox.Text         = r.Language ?? "";
        TzBox.SelectedItem       = r.Timezone;
        if (TzBox.SelectedItem is null) TzBox.Text = r.Timezone;  // preserve unknown values
        ScheduledHoursBox.Text   = r.ScheduledSendHours ?? "";
        AddressBox.Text          = r.Address ?? "";
        LocalityBox.Text         = r.LocalityName ?? "";
        LatBox.Text              = r.Latitude?.ToString("F7") ?? "";
        LonBox.Text              = r.Longitude?.ToString("F7") ?? "";
        MetarIcaoBox.Text        = r.MetarIcao ?? "";
        TafIcaoBox.Text          = r.TafIcao ?? "";
        TempUnitBox.SelectedItem     = r.TempUnit;
        PressureUnitBox.SelectedItem = r.PressureUnit;
        WindUnitBox.SelectedItem     = r.WindSpeedUnit;

        HideMessages();
        StationsGroup.Visibility  = Visibility.Collapsed;
        StationsGrid.ItemsSource  = null;
        BboxStatusText.Visibility = Visibility.Collapsed;
        SaveBtn.IsEnabled   = true;
        CancelBtn.IsEnabled = true;
        DeleteBtn.IsEnabled = true;
    }

    /// <summary>
    /// Resets the right pane to a blank new-recipient state with sensible defaults.
    /// </summary>
    /// <sideeffects>Clears all TextBoxes; sets default ComboBox selections; hides Messages and Stations sections.</sideeffects>
    private void ClearRightPane()
    {
        RecipientIdBox.Clear();
        NameBox.Clear();
        EmailBox.Clear();
        LanguageBox.Text       = App.DefaultLanguage;
        TzBox.SelectedItem     = "America/Chicago";
        ScheduledHoursBox.Text = "7";
        AddressBox.Clear();
        LocalityBox.Clear();
        LatBox.Clear();
        LonBox.Clear();
        MetarIcaoBox.Clear();
        TafIcaoBox.Clear();
        TempUnitBox.SelectedIndex     = 0;  // "F"
        PressureUnitBox.SelectedIndex = 0;  // "inHg"
        WindUnitBox.SelectedIndex     = 0;  // "mph"

        HideMessages();
        StationsGroup.Visibility  = Visibility.Collapsed;
        StationsGrid.ItemsSource  = null;
        BboxStatusText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Resets the right pane to the idle state: all fields blank, no defaults,
    /// Save/Cancel/Delete all disabled.  Used after a delete or cancel action.
    /// </summary>
    /// <sideeffects>Clears all TextBoxes and ComboBox selections; hides Messages and Stations sections; disables Save, Cancel, and Delete.</sideeffects>
    private void SetIdlePane()
    {
        RecipientIdBox.Clear();
        NameBox.Clear();
        EmailBox.Clear();
        LanguageBox.Clear();
        TzBox.SelectedItem = null;
        TzBox.Text         = "";
        ScheduledHoursBox.Clear();
        AddressBox.Clear();
        LocalityBox.Clear();
        LatBox.Clear();
        LonBox.Clear();
        MetarIcaoBox.Clear();
        TafIcaoBox.Clear();
        TempUnitBox.SelectedIndex     = -1;
        PressureUnitBox.SelectedIndex = -1;
        WindUnitBox.SelectedIndex     = -1;

        HideMessages();
        StationsGroup.Visibility  = Visibility.Collapsed;
        StationsGrid.ItemsSource  = null;
        BboxStatusText.Visibility = Visibility.Collapsed;
        SaveBtn.IsEnabled   = false;
        CancelBtn.IsEnabled = false;
        DeleteBtn.IsEnabled = false;
    }

    /// <summary>
    /// Displays a warning/error message in the Messages border panel (amber styling).
    /// </summary>
    /// <param name="message">The text to display.</param>
    /// <sideeffects>Sets <c>MessagesText.Text</c>; sets <c>MessagesBorder.Visibility</c> to Visible.</sideeffects>
    private void ShowMessage(string message)
    {
        MessagesText.Text               = message;
        MessagesBorder.Background       = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xCD));
        MessagesBorder.BorderBrush      = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
        MessagesText.Foreground         = new SolidColorBrush(Color.FromRgb(0x85, 0x64, 0x04));
        MessagesBorder.Visibility       = Visibility.Visible;
    }

    /// <summary>
    /// Displays a success message in the Messages border panel (green styling) that
    /// automatically dismisses itself after three seconds.
    /// </summary>
    /// <param name="message">The text to display.</param>
    /// <sideeffects>Sets <c>MessagesText.Text</c>; sets <c>MessagesBorder.Visibility</c> to Visible; schedules auto-hide.</sideeffects>
    private void ShowSuccessMessage(string message)
    {
        MessagesText.Text               = message;
        MessagesBorder.Background       = new SolidColorBrush(Color.FromRgb(0xD4, 0xED, 0xDA));
        MessagesBorder.BorderBrush      = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
        MessagesText.Foreground         = new SolidColorBrush(Color.FromRgb(0x15, 0x57, 0x24));
        MessagesBorder.Visibility       = Visibility.Visible;

        _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(HideMessages));
    }

    /// <summary>
    /// Hides the Messages border panel and clears its text.
    /// </summary>
    /// <sideeffects>Clears <c>MessagesText.Text</c>; sets <c>MessagesBorder.Visibility</c> to Collapsed.</sideeffects>
    private void HideMessages()
    {
        MessagesText.Text         = "";
        MessagesBorder.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Enables or disables the Look Up button and updates its label to reflect the in-progress state.
    /// </summary>
    /// <param name="inProgress"><see langword="true"/> while the async lookup is running; <see langword="false"/> when complete.</param>
    /// <sideeffects>Sets <c>LookUpBtn.IsEnabled</c> and <c>LookUpBtn.Content</c>.</sideeffects>
    private void SetLookupInProgress(bool inProgress)
    {
        LookUpBtn.IsEnabled = !inProgress;
        LookUpBtn.Content   = inProgress ? "Looking up…" : "Look Up";
    }

    /// <summary>
    /// Builds a sorted list of canonical IANA timezone IDs by converting each
    /// Windows timezone (from <see cref="TimeZoneInfo.GetSystemTimeZones"/>) to
    /// its IANA equivalent.  "UTC" is always included.
    /// </summary>
    private static List<string> BuildIanaTimeZoneList()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { "UTC" };
        foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var ianaId) && ianaId is not null)
                ids.Add(ianaId);
        }
        return ids.OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Returns <see langword="null"/> if <paramref name="s"/> is null or whitespace;
    /// otherwise returns the trimmed string.
    /// </summary>
    /// <param name="s">The input string.</param>
    /// <returns>Trimmed non-empty string, or <see langword="null"/>.</returns>
    private static string? NullIfEmpty(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Computes the Haversine great-circle distance in kilometres between two geographic coordinates.
    /// </summary>
    /// <param name="lat1">Latitude of the first point in decimal degrees.</param>
    /// <param name="lon1">Longitude of the first point in decimal degrees.</param>
    /// <param name="lat2">Latitude of the second point in decimal degrees.</param>
    /// <param name="lon2">Longitude of the second point in decimal degrees.</param>
    /// <returns>Distance in kilometres.</returns>
    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R    = 6371.0;
        var          dLat = (lat2 - lat1) * Math.PI / 180.0;
        var          dLon = (lon2 - lon1) * Math.PI / 180.0;
        var          a    = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                          + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                          * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
