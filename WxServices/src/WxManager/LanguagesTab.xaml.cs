using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;

using WxServices.Logging;

namespace WxManager;

/// <summary>
/// WX-166 Languages tab: a two-list shuttle over the ISO 639-1 registry. The left
/// list holds AllLanguages (not yet enabled); the right holds the enabled
/// (SupportedLanguages) set. WX-172: enabling a language no longer requires existing
/// templates — it triggers asynchronous generation on WxReport.Svc's next cycle, after
/// which the language reaches READY and is assignable to recipients. Disabling is refused
/// while any recipient is still assigned it.
/// </summary>
public partial class LanguagesTab : UserControl
{
    private readonly DbContextOptions<WeatherDataContext> _dbOptions;
    private List<Language> _all = new();   // every registry row, refreshed on each change

    // WX-212: while this tab is the visible one, poll the registry so a language that finishes
    // generating service-side (PENDING -> READY/BLOCKED, WX-172) updates in place — without the
    // operator having to switch tabs to re-trigger Loaded. Stopped on switch-away, so there is no
    // background DB query while the operator is on another tab.
    private const int AutoRefreshSeconds = 10;   // ~tracks the WxReport cycle that flips the status
    private readonly DispatcherTimer _refreshTimer;
    private bool _reloadInFlight;          // a reload (tick or explicit) is running right now
    private bool _reloadQueued;            // another reload was requested while one was running
    private bool _rebinding;               // suppress the SelectionChanged churn a rebind would fire

    public LanguagesTab()
    {
        _dbOptions = App.DbOptions;
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AutoRefreshSeconds) };
        _refreshTimer.Tick += async (_, _) => await ReloadAsync();   // ReloadAsync self-guards and never throws
        // The default WPF TabControl unloads a tab's content when you switch away and reloads it on
        // return, so Loaded/Unloaded bracket "this tab is visible": Loaded reloads (fresh on entry)
        // and starts the auto-refresh; Unloaded stops it, so there is no background DB query while
        // the operator is on another tab. On return, Loaded reloads by itself — no separate restart.
        Loaded += async (_, _) => { _refreshTimer.Start(); await ReloadAsync(); };
        Unloaded += (_, _) => _refreshTimer.Stop();
    }

    /// <summary>
    /// Reloads the registry and rebinds both lists, partitioned by IsEnabled. Single-flight with
    /// coalescing (WX-212): if a reload is already running (e.g. an auto-refresh tick), the new
    /// request just flags a final pass rather than running a second reload concurrently — so an
    /// explicit reload after an enable/disable still reflects the just-saved state, and two reloads
    /// never race on <see cref="_all"/>.
    /// </summary>
    private async Task ReloadAsync()
    {
        if (_reloadInFlight) { _reloadQueued = true; return; }
        _reloadInFlight = true;
        try
        {
            do
            {
                _reloadQueued = false;
                await using var ctx = new WeatherDataContext(_dbOptions);
                var langs = await ctx.Languages.OrderBy(l => l.DisplayName).ToListAsync();
                if (!IsLoaded) return;   // switched away / window closed mid-reload — drop this stale result (WX-212/CR)
                _all = langs;
                ApplyFilterAndBind();
            }
            while (_reloadQueued);
        }
        catch (Exception ex)
        {
            // One catch for the query AND the rebind: ReloadAsync must never throw, since it runs
            // from an async-void timer Tick / Loaded handler where an escaped exception would crash
            // WxManager instead of surfacing. Log as well as the UI message, so an auto-refresh
            // failure leaves a durable trace in the WxManager log and not just a transient
            // StatusText (CR) — matching the other tabs' reload-failure logging.
            Logger.Warn($"Languages tab reload failed: {ex.Message}");
            StatusText.Text = $"Could not load languages: {ex.Message}";
        }
        finally
        {
            _reloadInFlight = false;
        }
    }

    /// <summary>Rebinds the (filtered) AllLanguages list and the Supported list from <see cref="_all"/>.</summary>
    private void ApplyFilterAndBind()
    {
        var filter = FilterBox.Text?.Trim() ?? "";
        bool Match(Language l) => filter.Length == 0
            || l.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || l.IsoCode.Contains(filter, StringComparison.OrdinalIgnoreCase);

        // WX-212: remember the current selections by ISO code before the rebind — the bound items
        // are rebuilt every call (fresh row wrappers always; fresh Language entities on a reload),
        // so a periodic auto-refresh would otherwise deselect the row the operator is reading.
        var selectedAllIso = (AllList.SelectedItem as Language)?.IsoCode;
        var selectedSupIso = (SupportedList.SelectedItem as SupportedLanguageRow)?.Language.IsoCode;

        var allItems = _all.Where(l => !l.IsEnabled && Match(l)).ToList();
        // WX-172: the Supported list shows each enabled language's generation status; wrap
        // each in a row that carries the plain-text label and the operator guidance.
        var supportedItems = _all.Where(l => l.IsEnabled)
            .Select(l => new SupportedLanguageRow(l)).ToList();   // short; not filtered
        // Swapping ItemsSource and re-selecting fires SupportedList_SelectionChanged (first with
        // null when the source resets, then with the restored row), which would collapse-then-
        // re-show the detail panel — a visible blink on every 10s tick. Guard the whole rebind with
        // _rebinding and refresh the panel once at the end instead, so a live status change
        // (e.g. [Generating…] -> [Ready]) updates in place without flicker. Restore is by ISO since
        // the bound items are rebuilt each call.
        _rebinding = true;
        try
        {
            AllList.ItemsSource = allItems;
            SupportedList.ItemsSource = supportedItems;
            AllList.SelectedItem = selectedAllIso is null ? null
                : allItems.FirstOrDefault(l => l.IsoCode == selectedAllIso);
            SupportedList.SelectedItem = selectedSupIso is null ? null
                : supportedItems.FirstOrDefault(r => r.Language.IsoCode == selectedSupIso);
        }
        finally
        {
            _rebinding = false;
        }
        UpdateDetailPanel();
    }

    private void SupportedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_rebinding)        // a rebind updates the panel once at its end (UpdateDetailPanel)
            UpdateDetailPanel();
    }

    /// <summary>Shows the selected supported language's status + operator guidance in the detail
    /// panel, or collapses it when nothing is selected. Shared by the user's SelectionChanged and
    /// the end of a rebind, so the panel updates once, in place.</summary>
    private void UpdateDetailPanel()
    {
        if (SupportedList.SelectedItem is SupportedLanguageRow row)
        {
            DetailHeader.Text = $"{row.Display}  {row.StatusLabel}";
            DetailText.Text = row.StatusDetail;
            DetailPanel.Visibility = Visibility.Visible;
        }
        else
        {
            DetailPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilterAndBind();

    /// <summary>
    /// Enable: mark the selected language a SupportedLanguage. WX-172 inverts the old gate —
    /// enabling no longer requires pre-existing templates; instead it triggers asynchronous
    /// generation on WxReport.Svc's next cycle (the language starts PENDING and becomes
    /// assignable once READY). A previously BLOCKED/FAILED language is requeued (reset to
    /// PENDING) by <see cref="SetEnabledAsync"/>, which is the recovery path once a blocking
    /// token's renderer/code fix lands.
    /// </summary>
    private async void EnableBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AllList.SelectedItem is not Language lang)
        {
            StatusText.Text = "Select a language on the left to enable.";
            return;
        }
        await SetEnabledAsync(lang, true);
    }

    /// <summary>Disable: move the selected language back to AllLanguages, unless recipients are assigned it.</summary>
    private async void DisableBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SupportedList.SelectedItem is not SupportedLanguageRow row)
        {
            StatusText.Text = "Select a language on the right to disable.";
            return;
        }
        var lang = row.Language;
        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);
            var assigned = await ctx.Recipients.CountAsync(r => r.LanguageId == lang.Id);
            if (assigned > 0)
            {
                StatusText.Text = $"Can't disable “{lang.DisplayName}”: {assigned} recipient(s) are still assigned it. Reassign them on the Recipients tab first.";
                return;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not check recipient assignments: {ex.Message}";
            return;
        }
        await SetEnabledAsync(lang, false);
    }

    /// <summary>Persists the IsEnabled flip via the non-destructive <see cref="LanguageToggle"/>
    /// seam (re-fetched in a fresh context), requeueing a not-ready language for generation on
    /// enable and PRESERVING the curated templates on disable (WX-249), then reloads both lists.</summary>
    private async Task SetEnabledAsync(Language lang, bool enabled)
    {
        LanguageToggleResult result;
        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);
            result = await LanguageToggle.SetEnabledAsync(ctx, lang.Id, enabled);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
            return;
        }

        StatusText.Text = result.Outcome switch
        {
            LanguageToggleOutcome.NotFound =>
                "That language no longer exists — reloading.",
            LanguageToggleOutcome.BlockedByRecipients =>
                $"Can't disable “{lang.DisplayName}”: {result.AssignedRecipients} recipient(s) are still assigned it.",
            LanguageToggleOutcome.EnabledWillGenerate =>
                $"Enabled “{lang.DisplayName}” — its report templates are queued for generation on an upcoming report cycle; it becomes assignable to recipients once ready.",
            LanguageToggleOutcome.Enabled =>
                $"Enabled “{lang.DisplayName}” — ready; recipients can now be assigned it.",
            LanguageToggleOutcome.Disabled =>
                $"Disabled “{lang.DisplayName}” — its curated report templates are preserved; re-enabling reuses them (only any newly-added terms are generated).",
            _ => StatusText.Text,
        };
        await ReloadAsync();
    }
}

/// <summary>
/// A row in the Supported list: a <see cref="Language"/> plus the plain-text status label and
/// the operator-facing guidance for its <see cref="Language.GenerationState"/> (WX-172). The
/// label appears beside the name; the guidance fills the detail panel on selection. Presentation
/// lives here in WxManager, not on the entity — the entity exposes only the domain state.
/// </summary>
internal sealed class SupportedLanguageRow
{
    public SupportedLanguageRow(Language language) => Language = language;

    /// <summary>The underlying registry entity (the Disable handler acts on it).</summary>
    public Language Language { get; }

    /// <summary>Name + ISO code, e.g. "English (en)".</summary>
    public string Display => $"{Language.DisplayName} ({Language.IsoCode})";

    /// <summary>Short bracketed status shown beside the name in the list.</summary>
    public string StatusLabel => Language.GenerationState switch
    {
        LanguageGenerationState.Ready => "[Ready]",
        LanguageGenerationState.Pending => "[Generating…]",
        LanguageGenerationState.Blocked => "[Blocked]",
        LanguageGenerationState.Failed => "[Failed — retrying]",
        _ => "",
    };

    /// <summary>The operator guidance shown in the detail panel — what (if anything) to do next.</summary>
    public string StatusDetail => Language.GenerationState switch
    {
        LanguageGenerationState.Ready =>
            $"Ready — generated {Language.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC. Recipients can be assigned this language on the Recipients tab.",
        LanguageGenerationState.Pending =>
            "Generating — its report templates are queued and will be produced on an upcoming WxReport report cycle (one language is generated per cycle). No action needed; reload this tab to see the result.",
        LanguageGenerationState.Failed =>
            "Generation hit a transient error and retries automatically each cycle. If it persists, check the Claude API key and connectivity (the key lives in appsettings.local.json) and the WxReport log.\n\n"
            + Language.GenerationError,
        LanguageGenerationState.Blocked =>
            Language.GenerationError
            + "\n\nThis cannot be resolved from here — to shelve the language for now, click Disable.",
        _ => "",
    };
}