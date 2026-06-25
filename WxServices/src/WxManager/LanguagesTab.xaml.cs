using System.Windows;
using System.Windows.Controls;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;

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

    public LanguagesTab()
    {
        _dbOptions = App.DbOptions;
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    /// <summary>Reloads the registry and rebinds both lists, partitioned by IsEnabled.</summary>
    private async Task ReloadAsync()
    {
        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);
            _all = await ctx.Languages.OrderBy(l => l.DisplayName).ToListAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not load languages: {ex.Message}";
            return;
        }
        ApplyFilterAndBind();
    }

    /// <summary>Rebinds the (filtered) AllLanguages list and the Supported list from <see cref="_all"/>.</summary>
    private void ApplyFilterAndBind()
    {
        var filter = FilterBox.Text?.Trim() ?? "";
        bool Match(Language l) => filter.Length == 0
            || l.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || l.IsoCode.Contains(filter, StringComparison.OrdinalIgnoreCase);

        AllList.ItemsSource = _all.Where(l => !l.IsEnabled && Match(l)).ToList();
        // WX-172: the Supported list shows each enabled language's generation status; wrap
        // each in a row that carries the plain-text label and the operator guidance.
        SupportedList.ItemsSource = _all.Where(l => l.IsEnabled)
            .Select(l => new SupportedLanguageRow(l)).ToList();   // short; not filtered
        DetailPanel.Visibility = Visibility.Collapsed;            // selection cleared by the rebind
    }

    /// <summary>Shows the selected supported language's status + operator guidance in the detail panel.</summary>
    private void SupportedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

    /// <summary>Persists the IsEnabled flip (re-fetched in a fresh context), requeueing a
    /// not-ready language for generation on enable, and reloads both lists.</summary>
    private async Task SetEnabledAsync(Language lang, bool enabled)
    {
        bool willGenerate = false;
        try
        {
            await using var ctx = new WeatherDataContext(_dbOptions);
            var row = await ctx.Languages.FindAsync(lang.Id);
            if (row is null)
            {
                StatusText.Text = "That language no longer exists — reloading.";
                await ReloadAsync();
                return;
            }
            row.IsEnabled = enabled;
            if (enabled)
            {
                // WX-172: requeue a not-ready language so the service (re)generates it. A
                // BLOCKED/FAILED language (GenerationError set) is reset to PENDING — the
                // recovery path after a blocking token's renderer/code fix lands, since a
                // BLOCKED language has GeneratedAtUtc set and would otherwise never retry. A
                // READY language keeps its templates and stays READY; a never-generated one is
                // already PENDING. Disable leaves the generated state intact for quick re-enable.
                if (row.GenerationError is not null)
                {
                    row.GeneratedAtUtc = null;
                    row.GenerationError = null;
                }
                willGenerate = !row.IsReady;   // still not READY after the flip => generates next cycle
            }
            await ctx.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
            return;
        }
        StatusText.Text = enabled
            ? (willGenerate
                ? $"Enabled “{lang.DisplayName}” — its report templates will be generated on the next report cycle; it becomes assignable to recipients once ready."
                : $"Enabled “{lang.DisplayName}” — ready; recipients can now be assigned it.")
            : $"Disabled “{lang.DisplayName}”.";
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
            "Generating — its report templates will be produced on WxReport's next report cycle. No action needed; reload this tab to see the result.",
        LanguageGenerationState.Failed =>
            "Generation hit a transient error and retries automatically each cycle. If it persists, check the Claude API key and connectivity (appsettings.shared.json) and the WxReport log.\n\n"
            + Language.GenerationError,
        LanguageGenerationState.Blocked =>
            Language.GenerationError
            + "\n\nThis cannot be resolved from here — to shelve the language for now, click Disable.",
        _ => "",
    };
}