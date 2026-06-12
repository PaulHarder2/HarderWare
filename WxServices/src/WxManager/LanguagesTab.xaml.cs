using System.Windows;
using System.Windows.Controls;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;

using WxServices.Common;

namespace WxManager;

/// <summary>
/// WX-166 Languages tab: a two-list shuttle over the ISO 639-1 registry. The left
/// list holds AllLanguages (not yet enabled); the right holds SupportedLanguages
/// (enabled — the only ones a recipient may be assigned). Enabling requires that
/// localized report templates exist for the language (the <see cref="SupportedLanguages"/>
/// gate); disabling is refused while any recipient is still assigned it.
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
        SupportedList.ItemsSource = _all.Where(l => l.IsEnabled).ToList();   // short; not filtered
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilterAndBind();

    /// <summary>Enable: move the selected language into Supported, if report templates exist for it.</summary>
    private async void EnableBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AllList.SelectedItem is not Language lang)
        {
            StatusText.Text = "Select a language on the left to enable.";
            return;
        }
        if (!SupportedLanguages.HasTemplates(lang.IsoCode))
        {
            StatusText.Text = $"“{lang.DisplayName}” ({lang.IsoCode}) has no report templates yet, so it can't be enabled. Templates are added in WX-167.";
            return;
        }
        await SetEnabledAsync(lang, true);
    }

    /// <summary>Disable: move the selected language back to AllLanguages, unless recipients are assigned it.</summary>
    private async void DisableBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SupportedList.SelectedItem is not Language lang)
        {
            StatusText.Text = "Select a language on the right to disable.";
            return;
        }
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

    /// <summary>Persists the IsEnabled flip (re-fetched in a fresh context) and reloads both lists.</summary>
    private async Task SetEnabledAsync(Language lang, bool enabled)
    {
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
            await ctx.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
            return;
        }
        StatusText.Text = enabled
            ? $"Enabled “{lang.DisplayName}” — recipients can now be assigned it."
            : $"Disabled “{lang.DisplayName}”.";
        await ReloadAsync();
    }
}