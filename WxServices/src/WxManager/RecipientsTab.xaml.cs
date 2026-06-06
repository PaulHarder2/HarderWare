using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;

using WxServices.Logging;

namespace WxManager;

// ── UI data types ─────────────────────────────────────────────────────────────

/// <summary>Lightweight projection of a <see cref="Recipient"/> row used to populate the left-pane ListBox.</summary>
internal sealed class RecipientListItem
{
    /// <summary>Surrogate primary key from the <c>Recipients</c> table.</summary>
    public int DbId { get; set; }

    /// <summary>Application-level recipient identifier (e.g. <c>"john-en"</c>).</summary>
    public string RecipientId { get; set; } = "";

    /// <summary>Display name of the recipient.</summary>
    public string Name { get; set; } = "";

    /// <summary>Returns a human-readable string shown in the ListBox.</summary>
    /// <returns>A string in the form <c>"RecipientId — Name"</c>.</returns>
    public override string ToString() => $"{RecipientId} — {Name}";
}

/// <summary>One entry in the Locality ComboBox: an existing locality the recipient can be assigned to (WX-127).</summary>
internal sealed class LocalityComboItem
{
    /// <summary>Surrogate primary key from the <c>Localities</c> table.</summary>
    public long DbId { get; set; }

    /// <summary>Locality name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Returns the locality name shown in the ComboBox.</summary>
    public override string ToString() => Name;
}

/// <summary>One row in the Nearby Stations DataGrid, representing a single METAR station candidate.</summary>
internal sealed class StationRow
{
    /// <summary>ICAO station identifier.</summary>
    public string Icao { get; set; } = "";

    /// <summary>Human-readable station name from <c>WxStations</c>, or empty if not found.</summary>
    public string Name { get; set; } = "";

    /// <summary>Great-circle distance from the geocoded address in kilometres.</summary>
    public double DistKm { get; set; }

    /// <summary>Formatted distance string for display (e.g. <c>"12.3"</c>).</summary>
    public string DistKmStr => $"{DistKm:F1}";

    /// <summary>Number of METAR records in the local database for this station.</summary>
    public int MetarCount { get; set; }

    /// <summary>Formatted METAR count for display; shows <c>"—"</c> when zero.</summary>
    public string MetarCountStr => MetarCount == 0 ? "—" : MetarCount.ToString();

    /// <summary>Number of TAF records in the local database for this station.</summary>
    public int TafCount { get; set; }

    /// <summary>Formatted TAF count for display; shows <c>"—"</c> when zero.</summary>
    public string TafCountStr => TafCount == 0 ? "—" : TafCount.ToString();
}

/// <summary>DTO matching the Aviation Weather Center METAR API JSON response.</summary>
internal sealed class AwcMetar
{
    /// <summary>ICAO station identifier from the AWC response.</summary>
    [JsonPropertyName("icaoId")] public string? IcaoId { get; set; }

    /// <summary>Station latitude from the AWC response.</summary>
    [JsonPropertyName("lat")] public double? Lat { get; set; }

    /// <summary>Station longitude from the AWC response.</summary>
    [JsonPropertyName("lon")] public double? Lon { get; set; }
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

    // Original tooltips of the locality-mirrored fields, captured the first time
    // the lock is applied so unlocking can restore them (see ApplyLocalityLockState).
    private object? _metarIcaoTip, _tafIcaoTip, _hoursTip, _tzTip;

    /// <summary>
    /// Surrogate DB Id of the recipient currently loaded into the right pane,
    /// or <see langword="null"/> when creating a new recipient.
    /// </summary>
    private int? _currentRecipientDbId;

    /// <summary>
    /// Guards <see cref="LocalityCombo_SelectionChanged"/> against programmatic
    /// changes (loading a recipient, clearing the pane) so only user-driven
    /// selections trigger the mirrored-field preview.
    /// </summary>
    private bool _suppressLocalityComboEvents;

    /// <summary>
    /// The four mirrored field values as they stood before a locality-selection
    /// preview overwrote them, captured on the first preview and restored when
    /// the selection is cleared — so browsing localities in the combo can never
    /// destroy a recipient's own stations/timezone/hours.
    /// </summary>
    private (string Metar, string Taf, string Tz, string Hours)? _previewPristine;

    /// <summary>
    /// The recipient's stored <c>LocalityName</c> at load time. A combo text that
    /// still equals this label on Save is a pre-existing display label rendered
    /// back into the combo, not a membership request — without this, every save
    /// of a legacy unassigned recipient would re-trigger the create-locality
    /// prompt.
    /// </summary>
    private string? _loadedLocalityLabel;

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

        TempUnitBox.ItemsSource = new[] { "F", "C" };
        PressureUnitBox.ItemsSource = new[] { "inHg", "kPa" };
        WindUnitBox.ItemsSource = new[] { "mph", "kph" };
        TzBox.ItemsSource = IanaTimeZones.All();

        // Capture the mirrored fields' XAML tooltips once, so the locality lock
        // can swap in its hint and restore the originals reliably.
        _metarIcaoTip = MetarIcaoBox.ToolTip;
        _tafIcaoTip = TafIcaoBox.ToolTip;
        _hoursTip = ScheduledHoursBox.ToolTip;
        _tzTip = TzBox.ToolTip;

        ClearRightPane();   // show defaults; buttons stay disabled (set in XAML)

        Loaded += async (_, _) =>
        {
            await LoadRecipientListAsync();
            await LoadLocalityComboAsync();
        };
        // Localities can be created/renamed on the Localities tab while this tab
        // is hidden — refresh the combo whenever the tab becomes visible again.
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true) await LoadLocalityComboAsync();
        };
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
        SaveBtn.IsEnabled = true;
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
    /// Updates <c>LatBox</c>, <c>LonBox</c>, <c>LocalityCombo</c>, <c>MetarIcaoBox</c>,
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
        StationsGroup.Visibility = Visibility.Collapsed;
        StationsGrid.ItemsSource = null;
        BboxStatusText.Visibility = Visibility.Collapsed;

        try
        {
            // ── Geocode ──────────────────────────────────────────────────────────

            var geo = await AddressGeocoder.LookupAsync(address, _http, App.What3WordsApiKey);
            if (geo is null)
            {
                ShowMessage("Address could not be resolved. Check the format and try again, " +
                            "or fill in Locality and METAR/TAF ICAO directly.");
                return;
            }

            var (lat, lon, locality) = geo.Value;
            LatBox.Text = lat.ToString("F7");
            LonBox.Text = lon.ToString("F7");
            // Lat,lon direct entry returns an empty locality — preserve any value the user
            // already typed. A locality *member*'s combo selection is never overridden;
            // for unassigned recipients the geocoded name becomes the combo's typed text
            // (matching an existing locality assigns on Save; an unmatched name offers to
            // create one — WX-127).
            if (!string.IsNullOrWhiteSpace(locality) && LocalityCombo.SelectedItem is null)
                LocalityCombo.Text = locality;

            // A successful geocode implicitly begins editing; enable Save/Cancel.
            SaveBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;

            // Show the stations group with a searching advisory while the DB and AWC
            // queries run — the grid itself stays hidden until results are ready.
            StationsGroup.Visibility = Visibility.Visible;
            StationsSearchingText.Visibility = Visibility.Visible;
            StationsGrid.Visibility = Visibility.Collapsed;

            // ── Nearby METAR stations ─────────────────────────────────────────

            // ── Nearby stations from local WxStations table ───────────────────
            // Use a bbox pre-filter then Haversine for accuracy.

            double SearchRadiusKm = App.StationLookupRadiusKm;
            double latDelta = SearchRadiusKm / 111.0;
            double lonDelta = latDelta / Math.Cos(lat * Math.PI / 180.0);

            await using var ctx = new WeatherDataContext(_dbOptions);

            var nearbyStations = await ctx.WxStations
                .Where(s => s.Lat != null && s.Lon != null
                         && s.Lat >= lat - latDelta && s.Lat <= lat + latDelta
                         && s.Lon >= lon - lonDelta && s.Lon <= lon + lonDelta)
                .ToListAsync();

            // Take more candidates than we intend to display so that non-reporting
            // airports (which are numerous in the OurAirports dataset) can be filtered
            // out before the final 5 are chosen.
            var candidates = nearbyStations
                .Select(s => (Station: s, DistKm: HaversineKm(lat, lon, s.Lat!.Value, s.Lon!.Value)))
                .Where(x => x.DistKm <= SearchRadiusKm)
                .OrderBy(x => x.DistKm)
                .Take(App.MaxNearbyStationsInLookup)
                .ToList();

            if (candidates.Count == 0)
            {
                Logger.Info($"Station lookup: no WxStations found within {SearchRadiusKm:F0} km of ({lat:F4}, {lon:F4}).");
                StationsGroup.Visibility = Visibility.Collapsed;
                ShowMessage($"No stations found within {SearchRadiusKm:F0} km of the resolved coordinates.");
                return;
            }

            Logger.Info($"Station lookup: Nearest {candidates.Count} candidate(s) within {SearchRadiusKm:F0} km of ({lat:F4}, {lon:F4})");

            // ── DB counts (batched) ───────────────────────────────────────────

            var icaos = candidates.Select(c => c.Station.IcaoId).ToList();

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

            // ── AWC single-station fallback for stations with no local data ───
            // For stations absent from the local METAR table, query AWC directly
            // with a wider time window. If data exists, flag the station so the
            // fetch cycle will always request it individually.

            var rows = new List<StationRow>();
            foreach (var (station, distKm) in candidates)
            {
                var localCount = metarCounts.FirstOrDefault(x => x.Icao == station.IcaoId)?.Count ?? 0;
                var displayCount = localCount;

                if (localCount == 0)
                {
                    var awcCount = await FetchAwcSingleStationCountAsync(station.IcaoId);
                    if (awcCount > 0)
                    {
                        displayCount = awcCount;
                        if (station.AlwaysFetchDirect != true)
                        {
                            station.AlwaysFetchDirect = true;
                            await ctx.SaveChangesAsync();
                            Logger.Info($"  {station.IcaoId}: no local data; AWC returned {awcCount} record(s) — flagged AlwaysFetchDirect.");
                        }
                        else
                        {
                            Logger.Info($"  {station.IcaoId}: no local data; AWC returned {awcCount} record(s) (already flagged).");
                        }
                    }
                    else
                    {
                        Logger.Info($"  {station.IcaoId}: no local data and AWC returned nothing — suppressed.");
                    }
                }
                else
                {
                    Logger.Info($"  {station.IcaoId}: {localCount} local METAR(s), {tafCounts.FirstOrDefault(x => x.Icao == station.IcaoId)?.Count ?? 0} TAF(s).");
                }

                rows.Add(new StationRow
                {
                    Icao = station.IcaoId,
                    Name = FormatStationName(station),
                    DistKm = distKm,
                    MetarCount = displayCount,
                    TafCount = tafCounts.FirstOrDefault(x => x.Icao == station.IcaoId)?.Count ?? 0,
                });
            }

            // Suppress non-reporting airports so major METAR stations aren't pushed out.
            rows = rows.Where(r => r.MetarCount > 0).Take(App.MaxDisplayStations).ToList();

            if (rows.Count == 0)
            {
                Logger.Info("Station lookup: no active METAR stations survived the filter.");
                StationsGroup.Visibility = Visibility.Collapsed;
                ShowMessage($"No active METAR stations found within {SearchRadiusKm:F0} km of the resolved coordinates.");
                return;
            }

            Logger.Info($"Station lookup: displaying {rows.Count} station(s): " +
                        string.Join(", ", rows.Select(r => r.Icao)));

            StationsSearchingText.Visibility = Visibility.Collapsed;
            StationsGrid.Visibility = Visibility.Visible;
            StationsGrid.ItemsSource = rows;

            // Suggest MetarIcao only when the field is currently blank.
            if (string.IsNullOrEmpty(MetarIcaoBox.Text))
                MetarIcaoBox.Text = rows[0].Icao;

            // ── Fetch bounding-box advisory ───────────────────────────────────

            if (App.FetchRegion is { } fetchRegion)
            {
                var inBox = fetchRegion.Contains(lat, lon);

                if (inBox)
                {
                    BboxStatusText.Text = "Address is within the fetch bounding box.";
                    BboxStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0xDD, 0x88));
                }
                else
                {
                    BboxStatusText.Text = "Address is outside the fetch bounding box — " +
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
        var name = NameBox.Text.Trim();
        var email = EmailBox.Text.Trim();

        if (string.IsNullOrEmpty(recipientId)) { ShowMessage("Id is required."); return; }
        if (string.IsNullOrEmpty(name)) { ShowMessage("Name is required."); return; }
        if (string.IsNullOrEmpty(email)) { ShowMessage("Email is required."); return; }

        if (!Regex.IsMatch(recipientId, @"^[A-Za-z0-9_\-]+$"))
        { ShowMessage("Id may only contain letters, digits, hyphens, and underscores."); return; }

        try { _ = new MailAddress(email); }
        catch { ShowMessage("Email address is not valid."); return; }

        var tzValue = TzBox.Text.Trim().Length > 0 ? TzBox.Text.Trim() : "UTC";
        if (!IanaTimeZones.IsValid(tzValue))
        { ShowMessage($"\"{tzValue}\" is not a recognized IANA timezone ID."); return; }

        if (!ScheduledSendHoursFormat.TryValidate(ScheduledHoursBox.Text, out var badHourToken))
        { ShowMessage($"Scheduled hours must be integers 0\u201323, comma-separated (got \"{badHourToken}\")."); return; }

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
        if (lat is < -90 or > 90) { ShowMessage("Latitude must be between \u221290 and +90."); return; }
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
                    var icao = token.Trim().ToUpperInvariant();
                    var station = await ctx.WxStations.FirstOrDefaultAsync(s => s.IcaoId == icao);
                    if (station is null)
                    { ShowMessage($"METAR station \"{icao}\" was not found in the station database."); return; }
                    if (station.Lat is null || station.Lon is null)
                    { ShowMessage($"METAR station \"{icao}\" has no coordinates and cannot be used."); return; }
                    if (App.FetchRegion is { } fr1 && station.Lat.HasValue && station.Lon.HasValue
                        && !fr1.Contains(station.Lat.Value, station.Lon.Value))
                        bboxWarnings.Add($"{icao} is outside the fetch region");
                }
            }

            if (!string.IsNullOrWhiteSpace(tafRaw) && !tafRaw.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                var icao = tafRaw.ToUpperInvariant();
                var station = await ctx.WxStations.FirstOrDefaultAsync(s => s.IcaoId == icao);
                if (station is null)
                { ShowMessage($"TAF station \"{icao}\" was not found in the station database."); return; }
                if (station.Lat is null || station.Lon is null)
                { ShowMessage($"TAF station \"{icao}\" has no coordinates and cannot be used."); return; }
                if (App.FetchRegion is { } fr2 && station.Lat.HasValue && station.Lon.HasValue
                    && !fr2.Contains(station.Lat.Value, station.Lon.Value))
                    bboxWarnings.Add($"TAF station {icao} is outside the fetch region");
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

            var isNew = _currentRecipientDbId is null;

            r.RecipientId = recipientId;
            r.Name = name;
            r.Email = email;
            r.Language = NullIfEmpty(LanguageBox.Text);
            r.Timezone = TzBox.Text.Trim().Length > 0 ? TzBox.Text.Trim() : "UTC";
            r.ScheduledSendHours = NullIfEmpty(ScheduledHoursBox.Text);
            r.Address = NullIfEmpty(AddressBox.Text);
            r.Latitude = lat;
            r.Longitude = lon;
            // Uppercase on store: validation is case-insensitive, but downstream
            // comparisons (the no-TAF "NONE" sentinel, station matching) are not.
            // Pre-existing gap fixed inline during WX-127.
            r.MetarIcao = NullIfEmpty(MetarIcaoBox.Text)?.ToUpperInvariant();
            r.TafIcao = NullIfEmpty(TafIcaoBox.Text)?.ToUpperInvariant();
            r.TempUnit = TempUnitBox.SelectedItem?.ToString() ?? "F";
            r.PressureUnit = PressureUnitBox.SelectedItem?.ToString() ?? "inHg";
            r.WindSpeedUnit = WindUnitBox.SelectedItem?.ToString() ?? "mph";

            // ── Locality membership (WX-127) ──────────────────────────────────
            // The Locality combo is the single control for both membership and the
            // display label: selecting an existing locality assigns (the locality's
            // shared fields overwrite the recipient's on Save), typing an unmatched
            // name offers to create that locality seeded from this recipient, and a
            // blank combo means unassigned (an existing member is unassigned but
            // keeps its last-synced values; LocalityName stays for fallback display).
            // Resolve the target — queries and the create prompt only, no writes yet.
            var localityText = LocalityCombo.Text.Trim();
            var previousLocalityId = r.LocalityId;
            Locality? targetLocality = null;
            var keepStoredLabel = false;
            var saveAsLabel = false;

            if (LocalityCombo.SelectedItem is LocalityComboItem sel &&
                string.Equals(sel.Name, localityText, StringComparison.OrdinalIgnoreCase))
            {
                // The text cross-check matters: WPF text-search can auto-select a
                // prefix match ("Houston" → "Houston Heights") while the user is
                // typing a NEW name — honor the selection only while the visible
                // text still agrees with it.
                targetLocality = await ctx.Localities.FindAsync(sel.DbId);
                if (targetLocality is null)
                {
                    ShowMessage($"Locality \"{sel.Name}\" no longer exists — it may have been deleted " +
                                "on the Localities tab. Reselect or clear the Locality field, then save again.");
                    return;
                }
            }
            else if (localityText.Length > 0 && previousLocalityId is null &&
                     string.Equals(localityText, _loadedLocalityLabel ?? "", StringComparison.Ordinal))
            {
                // Unchanged stored display label rendered back into the combo — not
                // a membership request. Without this, every save of a legacy
                // unassigned recipient would re-trigger the create prompt below.
                keepStoredLabel = true;
            }
            else if (localityText.Length > 0)
            {
                targetLocality = await ctx.Localities.FirstOrDefaultAsync(l => l.Name == localityText);
                if (targetLocality is null)
                {
                    var create = MessageBox.Show(
                        $"Create new locality \"{localityText}\" with {name} as its first member?\n\n" +
                        $"Yes — create the locality; it inherits this recipient's stations, timezone, " +
                        "and scheduled hours, and its centroid starts at their coordinates.\n" +
                        $"No — keep \"{localityText}\" as a display label only; no locality is created.\n" +
                        "Cancel — go back without saving.",
                        "WxManager — Create Locality",
                        MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (create == MessageBoxResult.Cancel)
                    { ShowMessage("Save cancelled — adjust the Locality field and save again."); return; }

                    if (create == MessageBoxResult.No)
                    {
                        saveAsLabel = true;  // display label only — no membership
                    }
                    else
                    {
                        // Seed the founding member's current values upward (WX-127).
                        targetLocality = new Locality
                        {
                            Name = localityText,
                            MetarIcao = r.MetarIcao,
                            TafIcao = r.TafIcao,
                            Timezone = r.Timezone,
                            ScheduledSendHours = r.ScheduledSendHours,
                        };
                        ctx.Localities.Add(targetLocality);
                        Logger.Info($"Creating locality '{localityText}' seeded from recipient '{recipientId}'.");
                    }
                }
            }

            // ── Apply — atomically. The assign path needs two SaveChanges calls
            // (identities must exist before the FK is wired and centroids
            // recomputed), so a transaction keeps a failure in the second from
            // leaving an orphan locality or a recipient whose mirrored fields
            // contradict its FK. The create prompt above runs BEFORE this so no
            // DB transaction is held open across a modal dialog.
            await using var tx = await ctx.Database.BeginTransactionAsync();

            string localityNote;
            if (targetLocality is not null)
            {
                // Persist first so a newly created locality (and a new recipient) have
                // real identities before the FK is wired and centroids recomputed.
                await ctx.SaveChangesAsync();

                LocalityAssignment.Assign(r, targetLocality);

                var members = await ctx.Recipients
                    .Where(x => x.LocalityId == targetLocality.Id && x.Id != r.Id)
                    .ToListAsync();
                members.Add(r);  // r's FK is set in memory, not yet saved
                LocalityCentroid.Recompute(targetLocality, members);

                localityNote = $" Assigned to locality \"{targetLocality.Name}\".";
            }
            else
            {
                if (saveAsLabel)
                {
                    // Display-label-only path (the prompt's "No"): the typed text
                    // becomes LocalityName, and any prior membership ends.
                    r.LocalityName = localityText;
                    localityNote = previousLocalityId is not null
                        ? $" Removed from its locality; \"{localityText}\" saved as its display label."
                        : $" \"{localityText}\" saved as a display label (no locality).";
                }
                else if (previousLocalityId is not null)
                {
                    localityNote = " Removed from its locality — current settings kept.";
                }
                else
                {
                    // Blanking the combo on an unassigned recipient clears the stored
                    // display label — otherwise the old label silently resurrects on
                    // the next load. An unchanged label (keepStoredLabel) is left alone.
                    if (!keepStoredLabel && localityText.Length == 0)
                        r.LocalityName = null;
                    localityNote = "";
                }
                r.LocalityId = null;
            }

            // A move or unassign shrinks the previous locality — recompute its centroid.
            if (previousLocalityId is long prevId && prevId != targetLocality?.Id)
            {
                var prevLoc = await ctx.Localities.FindAsync(prevId);
                if (prevLoc is not null)
                {
                    var prevMembers = await ctx.Recipients
                        .Where(x => x.LocalityId == prevId && x.Id != r.Id)
                        .ToListAsync();
                    LocalityCentroid.Recompute(prevLoc, prevMembers);
                }
            }

            Logger.Info($"{(isNew ? "Inserting" : "Updating")} recipient '{recipientId}' ({name}, {email}).");
            await ctx.SaveChangesAsync();
            await tx.CommitAsync();
            _currentRecipientDbId = r.Id;
            DeleteBtn.IsEnabled = true;
            if (bboxWarnings.Count > 0)
            {
                Logger.Warn($"Recipient '{recipientId}' saved with bbox warnings: {string.Join("; ", bboxWarnings)}.");
                ShowMessage($"Saved.{localityNote} Note: {string.Join("; ", bboxWarnings)} — data may not be fetched for these stations.");
            }
            else
            {
                Logger.Info($"Recipient '{recipientId}' saved successfully.");
                ShowSuccessMessage($"Saved successfully.{localityNote}");
            }

            await LoadRecipientListAsync();
            await LoadLocalityComboAsync();
            // Re-render through the same path initial display uses, so the
            // mirrored fields' read-only state falls out automatically (WX-127).
            LoadRecipientIntoFields(r, keepMessages: true);
        }
        catch (Exception ex)
        {
            Logger.Error($"Save failed for recipient '{RecipientIdBox.Text.Trim()}'.", ex);
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

        Logger.Info($"Deleting recipient '{displayName}'.");
        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);
            var r = await ctx.Recipients.FindAsync(_currentRecipientDbId.Value);
            if (r != null)
            {
                // A member leaving shrinks its locality — recompute the centroid
                // over the remaining members (WX-127).
                if (r.LocalityId is long localityId)
                {
                    var loc = await ctx.Localities.FindAsync(localityId);
                    if (loc is not null)
                    {
                        var remaining = await ctx.Recipients
                            .Where(x => x.LocalityId == localityId && x.Id != r.Id)
                            .ToListAsync();
                        LocalityCentroid.Recompute(loc, remaining);
                    }
                }

                ctx.Recipients.Remove(r);
                await ctx.SaveChangesAsync();
                Logger.Info($"Recipient '{displayName}' deleted successfully.");
            }

            _currentRecipientDbId = null;
            SetIdlePane();
            await LoadRecipientListAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"Delete failed for recipient '{displayName}'.", ex);
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
    private void LoadRecipientIntoFields(Recipient r, bool keepMessages = false)
    {
        _currentRecipientDbId = r.Id;

        RecipientIdBox.Text = r.RecipientId;
        NameBox.Text = r.Name;
        EmailBox.Text = r.Email;
        LanguageBox.Text = r.Language ?? "";
        TzBox.SelectedItem = null;  // null first: a coerced-away assignment would keep the prior selection
        TzBox.SelectedItem = r.Timezone;
        if (TzBox.SelectedItem is null) TzBox.Text = r.Timezone;  // preserve unknown values
        ScheduledHoursBox.Text = r.ScheduledSendHours ?? "";
        AddressBox.Text = r.Address ?? "";
        _loadedLocalityLabel = r.LocalityName;
        _previewPristine = null;
        SetLocalityComboSelection(r.LocalityId, r.LocalityName);
        ApplyLocalityLockState(r.LocalityId is not null);
        LatBox.Text = r.Latitude?.ToString("F7") ?? "";
        LonBox.Text = r.Longitude?.ToString("F7") ?? "";
        MetarIcaoBox.Text = r.MetarIcao ?? "";
        TafIcaoBox.Text = r.TafIcao ?? "";
        TempUnitBox.SelectedItem = r.TempUnit;
        PressureUnitBox.SelectedItem = r.PressureUnit;
        WindUnitBox.SelectedItem = r.WindSpeedUnit;

        if (!keepMessages) HideMessages();
        StationsGroup.Visibility = Visibility.Collapsed;
        StationsGrid.ItemsSource = null;
        BboxStatusText.Visibility = Visibility.Collapsed;
        SaveBtn.IsEnabled = true;
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
        LanguageBox.Text = App.DefaultLanguage;
        TzBox.SelectedItem = App.DefaultTimezone;
        ScheduledHoursBox.Text = App.DefaultScheduledSendHour.ToString();
        AddressBox.Clear();
        _loadedLocalityLabel = null;
        _previewPristine = null;
        SetLocalityComboSelection(null, null);
        ApplyLocalityLockState(locked: false);
        LatBox.Clear();
        LonBox.Clear();
        MetarIcaoBox.Clear();
        TafIcaoBox.Clear();
        TempUnitBox.SelectedIndex = 0;  // "F"
        PressureUnitBox.SelectedIndex = 0;  // "inHg"
        WindUnitBox.SelectedIndex = 0;  // "mph"

        HideMessages();
        StationsGroup.Visibility = Visibility.Collapsed;
        StationsGrid.ItemsSource = null;
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
        TzBox.Text = "";
        ScheduledHoursBox.Clear();
        AddressBox.Clear();
        _loadedLocalityLabel = null;
        _previewPristine = null;
        SetLocalityComboSelection(null, null);
        ApplyLocalityLockState(locked: false);
        LatBox.Clear();
        LonBox.Clear();
        MetarIcaoBox.Clear();
        TafIcaoBox.Clear();
        TempUnitBox.SelectedIndex = -1;
        PressureUnitBox.SelectedIndex = -1;
        WindUnitBox.SelectedIndex = -1;

        HideMessages();
        StationsGroup.Visibility = Visibility.Collapsed;
        StationsGrid.ItemsSource = null;
        BboxStatusText.Visibility = Visibility.Collapsed;
        SaveBtn.IsEnabled = false;
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
        MessagesText.Text = message;
        MessagesBorder.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xCD));
        MessagesBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
        MessagesText.Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x64, 0x04));
        MessagesBorder.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Displays a success message in the Messages border panel (green styling) that
    /// automatically dismisses itself after three seconds.
    /// </summary>
    /// <param name="message">The text to display.</param>
    /// <sideeffects>Sets <c>MessagesText.Text</c>; sets <c>MessagesBorder.Visibility</c> to Visible; schedules auto-hide.</sideeffects>
    private void ShowSuccessMessage(string message)
    {
        MessagesText.Text = message;
        MessagesBorder.Background = new SolidColorBrush(Color.FromRgb(0xD4, 0xED, 0xDA));
        MessagesBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
        MessagesText.Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x57, 0x24));
        MessagesBorder.Visibility = Visibility.Visible;

        _ = Task.Delay(App.SuccessMessageDismissMs).ContinueWith(_ => Dispatcher.Invoke(HideMessages));
    }

    /// <summary>
    /// Hides the Messages border panel and clears its text.
    /// </summary>
    /// <sideeffects>Clears <c>MessagesText.Text</c>; sets <c>MessagesBorder.Visibility</c> to Collapsed.</sideeffects>
    private void HideMessages()
    {
        MessagesText.Text = "";
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
        LookUpBtn.Content = inProgress ? "Looking up…" : "Look Up";
    }

    /// <summary>
    /// Loads (or reloads) the Locality ComboBox's items from the <c>Localities</c>
    /// table, preserving the current selection or typed text across the refresh.
    /// </summary>
    /// <sideeffects>Executes an EF Core query; replaces <c>LocalityCombo.ItemsSource</c>.</sideeffects>
    private async Task LoadLocalityComboAsync()
    {
        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);
            var items = await ctx.Localities
                .OrderBy(l => l.Name)
                .Select(l => new LocalityComboItem { DbId = l.Id, Name = l.Name })
                .ToListAsync();

            _suppressLocalityComboEvents = true;
            try
            {
                var priorText = LocalityCombo.Text;
                var priorId = (LocalityCombo.SelectedItem as LocalityComboItem)?.DbId;
                LocalityCombo.ItemsSource = items;
                if (priorId is long id)
                    LocalityCombo.SelectedItem = items.FirstOrDefault(i => i.DbId == id);
                if (LocalityCombo.SelectedItem is null)
                    LocalityCombo.Text = priorText;
            }
            finally
            {
                _suppressLocalityComboEvents = false;
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: the combo just stays stale; the next refresh retries.
            Logger.Warn($"Failed to load localities for the Locality combo: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets the Locality ComboBox to reflect a recipient's membership without
    /// triggering the user-driven preview handler: selects the matching item for
    /// members, or shows the stored display label as plain text for unassigned
    /// recipients.
    /// </summary>
    /// <param name="localityId">The recipient's locality FK, or <see langword="null"/>.</param>
    /// <param name="localityName">The recipient's stored display label (fallback text).</param>
    /// <sideeffects>Sets <c>LocalityCombo.SelectedItem</c>/<c>.Text</c> under the suppression flag.</sideeffects>
    private void SetLocalityComboSelection(long? localityId, string? localityName)
    {
        _suppressLocalityComboEvents = true;
        try
        {
            LocalityCombo.SelectedItem = localityId is long id
                ? (LocalityCombo.ItemsSource as IEnumerable<LocalityComboItem>)?.FirstOrDefault(i => i.DbId == id)
                : null;
            if (LocalityCombo.SelectedItem is null)
                LocalityCombo.Text = localityName ?? "";
        }
        finally
        {
            _suppressLocalityComboEvents = false;
        }
    }

    /// <summary>
    /// Locks or unlocks the locality-mirrored fields (METAR/TAF stations, timezone,
    /// scheduled hours). For locality members the locality is authoritative
    /// (WX-125/WX-133), so these fields are read-only here and edited on the
    /// Localities tab; the lock is a preview when a not-yet-saved selection is
    /// pending and becomes the steady state after Save.
    /// </summary>
    /// <param name="locked"><see langword="true"/> to make the mirrored fields read-only.</param>
    /// <sideeffects>Sets IsReadOnly/IsEnabled and tooltips on the four mirrored controls.</sideeffects>
    private void ApplyLocalityLockState(bool locked)
    {
        const string hint = "Managed by the recipient's locality — edit on the Localities tab.";

        MetarIcaoBox.IsReadOnly = locked;
        TafIcaoBox.IsReadOnly = locked;
        ScheduledHoursBox.IsReadOnly = locked;
        TzBox.IsEnabled = !locked;  // an editable ComboBox's dropdown ignores IsReadOnly

        MetarIcaoBox.ToolTip = locked ? hint : _metarIcaoTip;
        TafIcaoBox.ToolTip = locked ? hint : _tafIcaoTip;
        ScheduledHoursBox.ToolTip = locked ? hint : _hoursTip;
        TzBox.ToolTip = locked ? hint : _tzTip;
    }

    /// <summary>
    /// Previews a user-driven Locality selection: an existing locality's shared
    /// values fill the mirrored fields (locked) so the user sees exactly what Save
    /// will commit; clearing or typing over the selection unlocks the fields again
    /// (typed new names seed the locality FROM these fields on Save).
    /// </summary>
    /// <param name="sender">The Locality ComboBox.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>May query the database and rewrite the mirrored fields; toggles the lock state; shows a banner.</sideeffects>
    private async void LocalityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLocalityComboEvents) return;

        if (LocalityCombo.SelectedItem is LocalityComboItem sel)
        {
            try
            {
                await using var ctx = new WeatherDataContext(_dbOptions);
                var loc = await ctx.Localities.FindAsync(sel.DbId);
                if (loc is null) return;

                // Re-check after the await (WX-134): a keystroke can clear or
                // change the selection while the query runs — text-search
                // auto-selects on prefix matches mid-typing, and the deselect
                // handler may already have run. Applying now would be a zombie
                // preview: fields locked with a stale locality's values while the
                // combo shows unrelated typed text (and a later Save would persist
                // those values onto the recipient).
                if (!ReferenceEquals(LocalityCombo.SelectedItem, sel)) return;

                // First preview for this recipient: remember their own values so
                // clearing the selection can put them back.
                _previewPristine ??= (MetarIcaoBox.Text, TafIcaoBox.Text, TzBox.Text, ScheduledHoursBox.Text);

                MetarIcaoBox.Text = loc.MetarIcao ?? "";
                TafIcaoBox.Text = loc.TafIcao ?? "";
                // Null first: assigning an item the ComboBox doesn't contain is
                // coerced away, which would silently keep the previous selection.
                TzBox.SelectedItem = null;
                TzBox.SelectedItem = loc.Timezone;
                if (TzBox.SelectedItem is null) TzBox.Text = loc.Timezone;
                ScheduledHoursBox.Text = loc.ScheduledSendHours ?? "";
                ApplyLocalityLockState(locked: true);

                ShowMessage($"Will assign to \"{loc.Name}\" on Save — stations, timezone, and scheduled hours come from the locality.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to preview locality: {ex.Message}");
            }
        }
        else
        {
            // Selection cleared (or typed over): undo the preview so the
            // recipient's own values — not the browsed locality's — are what an
            // unassigned Save (or a new-locality seed) sees.
            if (_previewPristine is { } pristine)
            {
                MetarIcaoBox.Text = pristine.Metar;
                TafIcaoBox.Text = pristine.Taf;
                // Null first — see the preview path: a coerced-away assignment
                // would silently keep the browsed locality's timezone selected.
                TzBox.SelectedItem = null;
                TzBox.SelectedItem = pristine.Tz;
                if (TzBox.SelectedItem is null) TzBox.Text = pristine.Tz;
                ScheduledHoursBox.Text = pristine.Hours;
                _previewPristine = null;
            }
            ApplyLocalityLockState(locked: false);
        }
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
    /// <summary>
    /// Builds the "Station" column text for the Nearby Stations grid:
    /// <c>"{Municipality}, {RegionAbbr}, {CountryAbbr}"</c>, omitting any missing
    /// component and falling back to the airport name when no location pieces
    /// are available.
    /// </summary>
    private static string FormatStationName(MetarParser.Data.Entities.WxStation s)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(s.Municipality)) parts.Add(s.Municipality!);
        if (!string.IsNullOrWhiteSpace(s.RegionAbbr)) parts.Add(s.RegionAbbr!);
        if (!string.IsNullOrWhiteSpace(s.CountryAbbr)) parts.Add(s.CountryAbbr!);

        if (parts.Count > 0) return string.Join(", ", parts);
        return s.Name ?? "";
    }

    /// <summary>
    /// Queries the Aviation Weather Center API for the number of recent METAR reports
    /// for a single station, used as a fallback when the station has no local database records.
    /// Returns 0 on any error or if no reports are found.
    /// </summary>
    private async Task<int> FetchAwcSingleStationCountAsync(string icao)
    {
        try
        {
            var url = $"{App.AwcMetarEndpoint}?ids={icao}&hours={App.AwcMetarHours}&format=json";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", App.UserAgent);
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return 0;
            var results = await resp.Content.ReadFromJsonAsync<AwcMetar[]>();
            return results?.Length ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                          + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                          * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}