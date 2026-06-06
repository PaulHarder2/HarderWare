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

/// <summary>Lightweight projection of a <see cref="Locality"/> row used to populate the left-pane ListBox.</summary>
internal sealed class LocalityListItem
{
    /// <summary>Surrogate primary key from the <c>Localities</c> table.</summary>
    public long DbId { get; set; }

    /// <summary>Locality name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Number of member recipients, shown alongside the name.</summary>
    public int MemberCount { get; set; }

    /// <summary>Returns the human-readable string shown in the ListBox.</summary>
    /// <returns>A string in the form <c>"Name (N)"</c>.</returns>
    public override string ToString() => $"{Name} ({MemberCount})";
}

/// <summary>One row in the Members list: a recipient belonging to the loaded locality.</summary>
internal sealed class MemberRow
{
    /// <summary>Surrogate primary key from the <c>Recipients</c> table.</summary>
    public int DbId { get; set; }

    /// <summary>Application-level recipient identifier (e.g. <c>"john-en"</c>).</summary>
    public string RecipientId { get; set; } = "";

    /// <summary>Display name of the recipient.</summary>
    public string Name { get; set; } = "";

    /// <summary>Human-readable string shown in the Members list.</summary>
    public string Display => $"{Name} — {RecipientId}";
}

/// <summary>One entry in the Add-member ComboBox: a recipient not assigned to any locality.</summary>
internal sealed class UnassignedRecipientItem
{
    /// <summary>Surrogate primary key from the <c>Recipients</c> table.</summary>
    public int DbId { get; set; }

    /// <summary>Application-level recipient identifier.</summary>
    public string RecipientId { get; set; } = "";

    /// <summary>Display name of the recipient.</summary>
    public string Name { get; set; } = "";

    /// <summary>Returns the human-readable string shown in the ComboBox.</summary>
    /// <returns>A string in the form <c>"RecipientId — Name"</c>.</returns>
    public override string ToString() => $"{RecipientId} — {Name}";
}

// ── User control ──────────────────────────────────────────────────────────────

/// <summary>
/// Localities tab content for WxManager (WX-127).
/// <para>
/// Left pane: scrollable list of all localities in the <c>Localities</c> database
/// table (with member counts), and a New button.
/// </para>
/// <para>
/// Right pane: the locality's shared fields (name, stations, timezone, scheduled
/// hours, read-only centroid) plus a Members section. Saving field edits
/// propagates them to every member via <see cref="LocalityAssignment.SyncMembers"/>;
/// adding or removing a member acts immediately through
/// <see cref="LocalityAssignment.Assign"/> / unassignment and
/// <see cref="LocalityCentroid.Recompute"/>, so the centroid visibly updates.
/// Deleting is blocked while members exist (the FK is <c>ON DELETE RESTRICT</c>).
/// </para>
/// </summary>
public partial class LocalitiesTab : UserControl
{
    // ── State ─────────────────────────────────────────────────────────────────

    private readonly DbContextOptions<WeatherDataContext> _dbOptions;

    /// <summary>
    /// Surrogate DB Id of the locality currently loaded into the right pane,
    /// or <see langword="null"/> when creating a new locality.
    /// </summary>
    private long? _currentLocalityDbId;

    /// <summary>
    /// Suppresses dirty-tracking while load/clear/idle methods set fields
    /// programmatically — only user edits enable Save (WX-134).
    /// </summary>
    private bool _suppressDirty;

    /// <summary>
    /// True after New is clicked, until a load/cancel/idle ends the new-locality
    /// editing session. Dirty-tracking only counts edits made while a record
    /// context is active (a loaded locality or New) — without this gate, stray
    /// WPF initialization events could enable Save on an idle pane (WX-134).
    /// </summary>
    private bool _newMode;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Initializes the control, fills the timezone ComboBox, and schedules the
    /// initial locality list load once the control is fully rendered.
    /// </summary>
    /// <sideeffects>
    /// Reads <see cref="App.DbOptions"/> to obtain the database connection.
    /// Subscribes to <see cref="FrameworkElement.Loaded"/> to trigger an async DB query.
    /// </sideeffects>
    public LocalitiesTab()
    {
        _dbOptions = App.DbOptions;
        InitializeComponent();
        TzBox.ItemsSource = IanaTimeZones.All();
        SetIdlePane();

        // Dirty-tracking (WX-134): Save enables only when a user edit occurs.
        DirtyTracking.Attach(MarkDirty,
            NameBox, MetarIcaoBox, TafIcaoBox, TzBox, ScheduledHoursBox);

        Loaded += async (_, _) => await LoadLocalityListAsync();
        // Memberships and labels can change on the Recipients tab while this tab
        // is hidden — on tab-return, refresh the list and reload the displayed
        // locality (members + unassigned pool) from the database. Discards
        // unsaved edits by design (ratified 2026-06-06, WX-134).
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is not true) return;
            try
            {
                await LoadLocalityListAsync();
                if (_currentLocalityDbId is long id)
                    await LoadLocalityIntoFieldsAsync(id);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Tab-return reload failed: {ex.Message}");
            }
        };
    }

    // ── Left pane: list ───────────────────────────────────────────────────────

    /// <summary>
    /// Loads (or reloads) the locality list with member counts, preserving the
    /// current selection where possible.
    /// </summary>
    /// <sideeffects>Executes EF Core queries; replaces <c>LocalityList.ItemsSource</c>.</sideeffects>
    private async Task LoadLocalityListAsync()
    {
        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);
            var items = await ctx.Localities
                .OrderBy(l => l.Name)
                .Select(l => new LocalityListItem
                {
                    DbId = l.Id,
                    Name = l.Name,
                    MemberCount = ctx.Recipients.Count(r => r.LocalityId == l.Id),
                })
                .ToListAsync();

            var currentId = _currentLocalityDbId;

            // Temporarily detach handler so programmatic selection change doesn't
            // reload fields; finally guarantees reattachment even if a binding throws.
            LocalityList.SelectionChanged -= LocalityList_SelectionChanged;
            try
            {
                LocalityList.ItemsSource = items;

                if (currentId.HasValue)
                {
                    var item = items.FirstOrDefault(i => i.DbId == currentId.Value);
                    if (item != null)
                        LocalityList.SelectedItem = item;
                }
            }
            finally
            {
                LocalityList.SelectionChanged += LocalityList_SelectionChanged;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load locality list: {ex.Message}",
                "WxManager — Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Loads the selected locality's fields and members into the right pane.
    /// </summary>
    /// <param name="sender">The locality ListBox.</param>
    /// <param name="e">Event arguments containing the new selection.</param>
    /// <sideeffects>Executes EF Core queries; populates all right-pane fields via <see cref="LoadLocalityIntoFieldsAsync"/>.</sideeffects>
    private async void LocalityList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LocalityList.SelectedItem is not LocalityListItem item)
            return;

        try
        {
            await LoadLocalityIntoFieldsAsync(item.DbId);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load locality: {ex.Message}",
                "WxManager — Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Left pane: New button ─────────────────────────────────────────────────

    /// <summary>
    /// Clears the right pane and sets up a blank new-locality state. The Members
    /// section stays disabled until the locality is saved — membership needs a row
    /// to point at.
    /// </summary>
    /// <param name="sender">The New button.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>Clears all right-pane fields; deselects the ListBox without triggering <see cref="LocalityList_SelectionChanged"/>.</sideeffects>
    private void NewBtn_Click(object sender, RoutedEventArgs e)
    {
        _currentLocalityDbId = null;

        LocalityList.SelectionChanged -= LocalityList_SelectionChanged;
        LocalityList.SelectedItem = null;
        LocalityList.SelectionChanged += LocalityList_SelectionChanged;

        _suppressDirty = true;
        try
        {
            NameBox.Clear();
            MetarIcaoBox.Clear();
            TafIcaoBox.Clear();
            TzBox.SelectedItem = App.DefaultTimezone;
            ScheduledHoursBox.Text = App.DefaultScheduledSendHour.ToString();
            CentroidBox.Text = "—";
            SetMembersSection(enabled: false, members: [], unassigned: []);
        }
        finally
        {
            _suppressDirty = false;
        }

        HideMessages();
        _newMode = true;
        SaveBtn.IsEnabled = false;  // enables on the first edit (WX-134)
        CancelBtn.IsEnabled = true;
        DeleteBtn.IsEnabled = false;
    }

    // ── Right pane: Save ──────────────────────────────────────────────────────

    /// <summary>
    /// Validates and saves the locality's shared fields, then propagates them to
    /// every member via <see cref="LocalityAssignment.SyncMembers"/>.
    /// </summary>
    /// <param name="sender">The Save button.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>
    /// Executes EF Core queries and updates; mutates member <c>Recipients</c> rows;
    /// refreshes the locality list; shows a banner message.
    /// </sideeffects>
    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { ShowMessage("Name is required."); return; }

        var tzValue = TzBox.Text.Trim().Length > 0 ? TzBox.Text.Trim() : "UTC";
        if (!IanaTimeZones.IsValid(tzValue))
        { ShowMessage($"\"{tzValue}\" is not a recognized IANA timezone ID."); return; }

        if (!ScheduledSendHoursFormat.TryValidate(ScheduledHoursBox.Text, out var badHourToken))
        { ShowMessage($"Scheduled hours must be integers 0–23, comma-separated (got \"{badHourToken}\")."); return; }

        var metarRaw = MetarIcaoBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(metarRaw))
        {
            foreach (var token in metarRaw.Split(','))
            {
                if (!Regex.IsMatch(token.Trim(), @"^[A-Z0-9]{4}$", RegexOptions.IgnoreCase))
                { ShowMessage($"METAR ICAO codes must be exactly 4 alphanumeric characters (got \"{token.Trim()}\")."); return; }
            }
        }

        // "NONE" is accepted for contract-parity with the Recipients tab (one
        // mirrored column, one spelling set); downstream it reads as null.
        var tafRaw = TafIcaoBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(tafRaw) && !tafRaw.Equals("NONE", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(tafRaw, @"^[A-Z0-9]{4}$", RegexOptions.IgnoreCase))
        { ShowMessage("TAF ICAO code must be exactly 4 alphanumeric characters, \"NONE\", or blank."); return; }

        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);

            // ── ICAO station validation (same rules as the Recipients tab) ───
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
                    if (App.FetchRegion is { } fr1 && !fr1.Contains(station.Lat.Value, station.Lon.Value))
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
                if (App.FetchRegion is { } fr2 && !fr2.Contains(station.Lat.Value, station.Lon.Value))
                    bboxWarnings.Add($"TAF station {icao} is outside the fetch region");
            }

            // ── Name uniqueness ───────────────────────────────────────────────
            var nameTaken = await ctx.Localities.AnyAsync(l =>
                l.Name == name && (_currentLocalityDbId == null || l.Id != _currentLocalityDbId.Value));
            if (nameTaken)
            { ShowMessage($"A locality named \"{name}\" already exists."); return; }

            Locality loc;
            if (_currentLocalityDbId is null)
            {
                loc = new Locality();
                ctx.Localities.Add(loc);
            }
            else
            {
                loc = await ctx.Localities.FindAsync(_currentLocalityDbId.Value)
                    ?? throw new InvalidOperationException(
                        $"Locality DB Id={_currentLocalityDbId} not found — it may have been deleted externally.");
            }

            var isNew = _currentLocalityDbId is null;

            loc.Name = name;
            // Uppercase on store: validation is case-insensitive, but downstream
            // comparisons (the no-TAF "NONE" sentinel, station matching) are not —
            // and the mirror fans these values out to every member.
            loc.MetarIcao = NullIfEmpty(MetarIcaoBox.Text)?.ToUpperInvariant();
            loc.TafIcao = NullIfEmpty(TafIcaoBox.Text)?.ToUpperInvariant();
            loc.Timezone = tzValue;
            loc.ScheduledSendHours = NullIfEmpty(ScheduledHoursBox.Text);

            // Propagate the (possibly changed) shared fields to every member —
            // the locality is authoritative (WX-125/WX-133).
            var members = isNew
                ? []
                : await ctx.Recipients.Where(r => r.LocalityId == loc.Id).ToListAsync();
            LocalityAssignment.SyncMembers(loc, members);

            Logger.Info($"{(isNew ? "Inserting" : "Updating")} locality '{name}' ({members.Count} member(s) synced).");
            await ctx.SaveChangesAsync();
            _currentLocalityDbId = loc.Id;
            DeleteBtn.IsEnabled = true;

            var propagation = members.Count > 0
                ? $" Stations, timezone, and hours propagated to {members.Count} member(s)."
                : "";
            if (bboxWarnings.Count > 0)
            {
                Logger.Warn($"Locality '{name}' saved with bbox warnings: {string.Join("; ", bboxWarnings)}.");
                ShowMessage($"Saved.{propagation} Note: {string.Join("; ", bboxWarnings)} — data may not be fetched for these stations.");
            }
            else
            {
                Logger.Info($"Locality '{name}' saved successfully.");
                ShowSuccessMessage($"Saved successfully.{propagation}");
            }

            await LoadLocalityListAsync();
            await LoadLocalityIntoFieldsAsync(loc.Id, keepMessages: true);
        }
        catch (Exception ex)
        {
            Logger.Error($"Save failed for locality '{name}'.", ex);
            ShowMessage($"Save failed: {ex.Message}");
        }
    }

    // ── Right pane: Delete ────────────────────────────────────────────────────

    /// <summary>
    /// Deletes the loaded locality after a confirmation popup that always names
    /// the consequence: members, if any, are unassigned (keeping their last-synced
    /// values) and the locality is removed — both in one atomic save. The
    /// database's <c>ON DELETE RESTRICT</c> rule still stands; the UI satisfies it
    /// deliberately by unassigning first. (Redesigned in WX-134: the original
    /// blocked-while-members-exist banner read as if Delete acted on a member.)
    /// </summary>
    /// <param name="sender">The Delete button.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>May unassign member recipients and execute an EF Core DELETE; clears the right pane and refreshes the list.</sideeffects>
    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLocalityDbId is null) return;

        var name = NameBox.Text.Trim();
        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);

            var memberCount = await ctx.Recipients.CountAsync(r => r.LocalityId == _currentLocalityDbId.Value);
            var memberNote = memberCount > 0
                ? $"\n\nIts {memberCount} member(s) will be unassigned — they keep their stations, " +
                  "timezone, and scheduled hours, but their Locality label is cleared."
                : "";
            var result = MessageBox.Show(
                $"Delete locality \"{name}\"?{memberNote}\n\nThis cannot be undone.",
                "WxManager — Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            Logger.Info($"Deleting locality '{name}' ({memberCount} member(s) to unassign).");
            var loc = await ctx.Localities.FindAsync(_currentLocalityDbId.Value);
            if (loc != null)
            {
                // Unassign members, then delete — one SaveChanges, so the pair is
                // atomic and the FK's RESTRICT is satisfied within the same commit.
                var members = await ctx.Recipients.Where(r => r.LocalityId == loc.Id).ToListAsync();
                foreach (var member in members)
                {
                    member.LocalityId = null;    // stations/timezone/hours stay as last synced
                    member.LocalityName = null;  // a lingering label reads as still-assigned
                }

                ctx.Localities.Remove(loc);
                await ctx.SaveChangesAsync();
                Logger.Info($"Locality '{name}' deleted; {members.Count} member(s) unassigned.");
            }

            _currentLocalityDbId = null;
            SetIdlePane();
            await LoadLocalityListAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"Delete failed for locality '{name}'.", ex);
            ShowMessage($"Delete failed: {ex.Message}");
        }
    }

    // ── Right pane: Cancel ────────────────────────────────────────────────────

    /// <summary>
    /// Discards any unsaved edits and returns the form to the idle (no-selection) state.
    /// </summary>
    /// <param name="sender">The Cancel button.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>Clears all right-pane fields; deselects the ListBox; disables Save, Cancel, and Delete.</sideeffects>
    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _currentLocalityDbId = null;

        LocalityList.SelectionChanged -= LocalityList_SelectionChanged;
        LocalityList.SelectedItem = null;
        LocalityList.SelectionChanged += LocalityList_SelectionChanged;

        SetIdlePane();
    }

    // ── Members: Add / Remove ─────────────────────────────────────────────────

    /// <summary>
    /// Assigns the selected unassigned recipient to the loaded locality. Acts
    /// immediately (no Save needed): mirrors the shared fields onto the recipient,
    /// recomputes the centroid, and refreshes the pane so the change is visible.
    /// </summary>
    /// <param name="sender">The Add button.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>Executes EF Core updates; mutates the recipient row and locality centroid; refreshes the pane.</sideeffects>
    private async void AddMemberBtn_Click(object sender, RoutedEventArgs e)
    {
        Logger.Debug($"AddMemberBtn_Click fired (locality={_currentLocalityDbId?.ToString() ?? "none"}).");
        if (_currentLocalityDbId is null)
        { ShowMessage("Select (or save) a locality first."); return; }
        if (AddMemberCombo.SelectedItem is not UnassignedRecipientItem pick)
        { ShowMessage("Select a recipient to add."); return; }

        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);
            var loc = await ctx.Localities.FindAsync(_currentLocalityDbId.Value)
                ?? throw new InvalidOperationException("Locality not found — it may have been deleted externally.");
            var recipient = await ctx.Recipients.FindAsync(pick.DbId)
                ?? throw new InvalidOperationException("Recipient not found — it may have been deleted externally.");

            LocalityAssignment.Assign(recipient, loc);

            var members = await ctx.Recipients
                .Where(r => r.LocalityId == loc.Id || r.Id == recipient.Id)
                .ToListAsync();
            LocalityCentroid.Recompute(loc, members);

            await ctx.SaveChangesAsync();
            Logger.Info($"Recipient '{recipient.RecipientId}' added to locality '{loc.Name}'; centroid recomputed.");

            await LoadLocalityListAsync();
            await LoadLocalityIntoFieldsAsync(loc.Id, keepMessages: true);
            ShowSuccessMessage($"{recipient.Name} added — stations, timezone, and hours applied; centroid updated.");
        }
        catch (Exception ex)
        {
            Logger.Error("Add member failed.", ex);
            ShowMessage($"Add member failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the clicked member from the loaded locality. The recipient keeps
    /// their last-synced values (they simply stop tracking the locality); the
    /// centroid is recomputed over the remaining members.
    /// </summary>
    /// <param name="sender">The per-row Remove button (its Tag carries the <see cref="MemberRow"/>).</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>Executes EF Core updates; mutates the recipient row and locality centroid; refreshes the pane.</sideeffects>
    private async void RemoveMemberBtn_Click(object sender, RoutedEventArgs e)
    {
        // Entry + bail diagnostics (WX-134): a guard `return` with no banner and no
        // log line is how a click dies invisibly — which made a field debugging
        // session needlessly mysterious.
        Logger.Debug($"RemoveMemberBtn_Click fired (locality={_currentLocalityDbId?.ToString() ?? "none"}).");
        if (_currentLocalityDbId is null)
        { ShowMessage("Select a locality first."); return; }
        if (sender is not Button { Tag: MemberRow row })
        {
            Logger.Warn("Remove click carried no member-row context — ignored.");
            ShowMessage("Could not identify the member to remove — reselect the locality and try again.");
            return;
        }

        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);
            var loc = await ctx.Localities.FindAsync(_currentLocalityDbId.Value)
                ?? throw new InvalidOperationException("Locality not found — it may have been deleted externally.");
            var recipient = await ctx.Recipients.FindAsync(row.DbId);
            if (recipient is null) return;

            // Unassign: membership ends; the mirrored stations/timezone/hours stay
            // as last synced, but the locality label is cleared — a lingering
            // label reads as still-assigned (WX-134 field finding).
            recipient.LocalityId = null;
            recipient.LocalityName = null;

            var remaining = await ctx.Recipients
                .Where(r => r.LocalityId == loc.Id && r.Id != recipient.Id)
                .ToListAsync();
            LocalityCentroid.Recompute(loc, remaining);

            await ctx.SaveChangesAsync();
            Logger.Info($"Recipient '{recipient.RecipientId}' removed from locality '{loc.Name}'; centroid recomputed.");

            await LoadLocalityListAsync();
            await LoadLocalityIntoFieldsAsync(loc.Id, keepMessages: true);
            ShowSuccessMessage($"{recipient.Name} removed — stations, timezone, and hours kept; locality label cleared; centroid updated.");
        }
        catch (Exception ex)
        {
            Logger.Error("Remove member failed.", ex);
            ShowMessage($"Remove member failed: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the locality with the given Id (plus its members and the unassigned-
    /// recipient pool) into the right pane.
    /// </summary>
    /// <param name="localityId">Surrogate key of the locality to load.</param>
    /// <param name="keepMessages">When <see langword="true"/>, the Messages banner is left as-is (used after Save/Add/Remove so their feedback survives the refresh).</param>
    /// <sideeffects>Executes EF Core queries; sets all right-pane fields; enables the buttons and Members section.</sideeffects>
    private async Task LoadLocalityIntoFieldsAsync(long localityId, bool keepMessages = false)
    {
        await using var ctx = new WeatherDataContext(_dbOptions);
        var loc = await ctx.Localities.FindAsync(localityId);
        if (loc == null)
        {
            // Deleted externally (or on another tab) — fall back to idle rather
            // than leaving a stale pane.
            _currentLocalityDbId = null;
            SetIdlePane();
            return;
        }

        var members = await ctx.Recipients
            .Where(r => r.LocalityId == localityId)
            .OrderBy(r => r.RecipientId)
            .Select(r => new MemberRow { DbId = r.Id, RecipientId = r.RecipientId, Name = r.Name })
            .ToListAsync();

        var unassigned = await ctx.Recipients
            .Where(r => r.LocalityId == null)
            .OrderBy(r => r.RecipientId)
            .Select(r => new UnassignedRecipientItem { DbId = r.Id, RecipientId = r.RecipientId, Name = r.Name })
            .ToListAsync();

        _currentLocalityDbId = loc.Id;
        _newMode = false;

        _suppressDirty = true;
        try
        {
            NameBox.Text = loc.Name;
            MetarIcaoBox.Text = loc.MetarIcao ?? "";
            TafIcaoBox.Text = loc.TafIcao ?? "";
            TzBox.SelectedItem = null;  // null first: a coerced-away assignment would keep the prior selection
            TzBox.SelectedItem = loc.Timezone;
            if (TzBox.SelectedItem is null) TzBox.Text = loc.Timezone;  // preserve unknown values
            ScheduledHoursBox.Text = loc.ScheduledSendHours ?? "";
            CentroidBox.Text = loc.CentroidLat is double lat && loc.CentroidLon is double lon
                ? $"{lat:F4}, {lon:F4}"
                : "—";

            SetMembersSection(enabled: true, members, unassigned);
        }
        finally
        {
            _suppressDirty = false;
        }

        if (!keepMessages) HideMessages();
        SaveBtn.IsEnabled = false;  // clean state — enables on the first edit (WX-134)
        CancelBtn.IsEnabled = true;
        DeleteBtn.IsEnabled = true;
    }

    /// <summary>
    /// Configures the Members section: its lists, header count, and whether it is
    /// usable (it is disabled for an unsaved new locality — membership needs a row
    /// to point at).
    /// </summary>
    /// <param name="enabled">Whether Add/Remove are available.</param>
    /// <param name="members">Current member rows.</param>
    /// <param name="unassigned">Recipients available to add (no locality).</param>
    /// <sideeffects>Sets list ItemsSources, the GroupBox header, the hint text, and IsEnabled states.</sideeffects>
    private void SetMembersSection(bool enabled, List<MemberRow> members, List<UnassignedRecipientItem> unassigned)
    {
        MembersList.ItemsSource = members;
        AddMemberCombo.ItemsSource = unassigned;
        AddMemberCombo.SelectedItem = null;
        MembersGroup.Header = members.Count > 0 ? $"Members ({members.Count})" : "Members";
        MembersHintText.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        MembersList.IsEnabled = enabled;
        AddMemberCombo.IsEnabled = enabled;
        AddMemberBtn.IsEnabled = enabled;
    }

    /// <summary>
    /// Resets the right pane to the idle state: all fields blank, Members section
    /// disabled, Save/Cancel/Delete all disabled.
    /// </summary>
    /// <sideeffects>Clears all fields and list sources; disables buttons; hides messages.</sideeffects>
    private void SetIdlePane()
    {
        _newMode = false;
        _suppressDirty = true;
        try
        {
            NameBox.Clear();
            MetarIcaoBox.Clear();
            TafIcaoBox.Clear();
            TzBox.SelectedItem = null;
            TzBox.Text = "";
            ScheduledHoursBox.Clear();
            CentroidBox.Clear();
            SetMembersSection(enabled: false, members: [], unassigned: []);
            MembersHintText.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _suppressDirty = false;
        }

        HideMessages();
        SaveBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        DeleteBtn.IsEnabled = false;
    }

    /// <summary>
    /// Enables Save (and Cancel) on a user edit — no-op while a load/clear path
    /// is setting fields programmatically (WX-134 dirty-tracking).
    /// </summary>
    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_currentLocalityDbId is null && !_newMode) return;  // no record context — not an edit
        SaveBtn.IsEnabled = true;
        CancelBtn.IsEnabled = true;
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
    /// automatically dismisses itself after a few seconds.
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
    /// Returns <see langword="null"/> if <paramref name="s"/> is null or whitespace;
    /// otherwise returns the trimmed string.
    /// </summary>
    /// <param name="s">The input string.</param>
    /// <returns>Trimmed non-empty string, or <see langword="null"/>.</returns>
    private static string? NullIfEmpty(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}