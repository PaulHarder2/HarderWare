using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;

using WxServices.Common.TranslationQa;

namespace WxManager;

/// <summary>
/// WX-233 — a per-language editor for the report vocabulary (<c>LanguageTemplates</c>). Pick a language,
/// edit each token's Selected-Language phrase and <c>Note</c> in a grid, and Save. Each edit runs through a
/// chain of validators live (Mirion pattern): the cell flags invalid input and the Save button stays
/// disabled until every change is valid. Token, Context, and Representable are read-only (cross-language
/// attributes — changed via migration, not here).
///
/// WX-258 — the <c>English</c> source column is editable in place (English is still not a selector entry —
/// no English "language" is picked). An English edit writes the shared <c>en</c> <c>LanguageTemplates</c>
/// row and must keep that token's original <c>{n}</c> placeholders: every target phrase is validated
/// against them, so changing the placeholder set here would silently desync all languages (that stays a
/// migration). The selector lists every non-English language with templates, INCLUDING dormant (disabled)
/// ones (WX-249 keeps their curated rows), marking them "— disabled". A pure recasing of English (the
/// WX-258 daypart intent) does not change meaning, so targets stay valid; but a *meaningful* English
/// reword shifts the source every target was QA'd against, and the save deliberately does not re-stamp
/// the targets — so re-run QA on affected targets after such an edit (auto-invalidation on an en change is
/// a possible follow-up, not built here).
/// </summary>
public partial class VocabularyTab : UserControl
{
    private List<VocabEditRow> _rows = new();
    private string _currentIso = "";
    private QaRerunStatus? _lastRerunStatus; // last rerun status seen for _currentIso (gates the reload-on-press)
    private int _loadVersion; // bumped per load; only the latest load may publish (guards overlapping selections)

    public VocabularyTab()
    {
        InitializeComponent();
        // Only IsVisibleChanged drives the (re)load — it fires on first show and on each return to the tab.
        // A dirty-guard in LoadLanguagesAsync keeps a return-to-tab from discarding unsaved edits.
        IsVisibleChanged += async (_, e) => { if (e.NewValue is true) await LoadLanguagesAsync(); };

        // WX-235: subscribe to the shared rerun coordinator only while this tab is loaded (no leak).
        // Unsubscribe-then-subscribe is idempotent — WPF can raise Loaded without a matching Unloaded.
        Loaded += (_, _) =>
        {
            App.QaRerunCoordinator.StatusChanged -= OnRerunStatusChanged;
            App.QaRerunCoordinator.StatusChanged += OnRerunStatusChanged;
        };
        Unloaded += (_, _) => App.QaRerunCoordinator.StatusChanged -= OnRerunStatusChanged;
    }

    // ── WX-235: "Rerun QA" wiring ──
    private void OnRerunStatusChanged(string iso)
    {
        if (!string.Equals(iso, _currentIso, StringComparison.Ordinal))
            return;
        var v = App.QaRerunCoordinator.StatusFor(iso);
        var running = v.Status == QaRerunStatus.Running;
        // Lock the grid while this language is regenerating (any edit would immediately re-stale the package).
        VocabGrid.IsReadOnly = running;
        // Reload-from-DB on press: when the rerun begins, snap the grid to the committed DB state — dropping any
        // unsaved edits — so what the service judges is exactly what is shown.
        if (running && _lastRerunStatus != QaRerunStatus.Running)
            _ = LoadTemplatesAsync(iso);
        // Gate saving too, not just editing: a save mid-rerun would write to the very DB the service is judging.
        if (running)
            SaveButton.IsEnabled = false;
        else
            UpdateSaveState();   // run ended — restore Save's enabled state from the current dirty/valid counts
        _lastRerunStatus = v.Status;
    }

    private async Task LoadLanguagesAsync()
    {
        if (_rows.Any(r => r.IsDirty))
            return; // preserve in-progress edits across tab switches — don't silently reload over them

        var current = (LangSelector.SelectedItem as LangItem)?.Iso ?? _currentIso;
        try
        {
            await using var ctx = new WeatherDataContext(App.DbOptions);
            // English is not a selector entry — it is the shared source, edited in place in its own column
            // (WX-258), not picked as a "language" to translate. Every OTHER language with templates is
            // listed, INCLUDING dormant (disabled) ones — WX-249 keeps their curated rows so they stay
            // curatable; the item marks them "— disabled" so live vs dormant is obvious (WX-261-adjacent).
            var langs = await ctx.Languages
                .Where(l => l.IsoCode != "en" && ctx.LanguageTemplates.Any(t => t.LanguageId == l.Id))
                .OrderBy(l => l.IsoCode)
                .Select(l => new LangItem(l.IsoCode, l.DisplayName, l.IsEnabled))
                .ToListAsync();

            LangSelector.SelectionChanged -= LangSelector_SelectionChanged;
            LangSelector.ItemsSource = langs;
            LangSelector.DisplayMemberPath = nameof(LangItem.Display);
            LangSelector.SelectedItem = langs.FirstOrDefault(l => l.Iso == current) ?? langs.FirstOrDefault();
            LangSelector.SelectionChanged += LangSelector_SelectionChanged;

            if (LangSelector.SelectedItem is LangItem sel)
                await LoadTemplatesAsync(sel.Iso);
            else
            {
                ClearRows();
                StatusText.Text = "No generated languages found.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error loading languages: " + ex.Message;
        }
    }

    private async void LangSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LangSelector.SelectedItem is not LangItem sel || sel.Iso == _currentIso)
            return;

        if (_rows.Any(r => r.IsDirty) &&
            MessageBox.Show($"Discard unsaved changes to {_currentIso}?", "Unsaved changes",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            // Revert the selector to the language still being edited.
            LangSelector.SelectionChanged -= LangSelector_SelectionChanged;
            LangSelector.SelectedItem = (LangSelector.ItemsSource as IEnumerable<LangItem>)?
                .FirstOrDefault(l => l.Iso == _currentIso);
            LangSelector.SelectionChanged += LangSelector_SelectionChanged;
            return;
        }

        await LoadTemplatesAsync(sel.Iso);
    }

    private async Task LoadTemplatesAsync(string iso)
    {
        var version = ++_loadVersion;
        try
        {
            List<VocabEditRow> newRows;
            await using (var ctx = new WeatherDataContext(App.DbOptions))
            {
                var enSource = await ctx.LanguageTemplates
                    .Where(t => t.Language!.IsoCode == "en")
                    .Select(t => new { t.Token, t.Id, t.Phrase })
                    .ToDictionaryAsync(t => t.Token, t => (Id: t.Id, Phrase: t.Phrase));

                newRows = (await ctx.LanguageTemplates.Include(t => t.Language)
                        .Where(t => t.Language!.IsoCode == iso)
                        .OrderBy(t => t.Token)
                        .AsNoTracking()
                        .ToListAsync())
                    .Select(t =>
                    {
                        // The token's en row supplies the editable English source + the id an English edit
                        // writes back to (WX-258). Absent en row (shouldn't happen — en is the full baseline)
                        // → id 0, English "", so the row simply can't be English-edited.
                        var en = enSource.TryGetValue(t.Token, out var e) ? e : (Id: 0L, Phrase: "");
                        return new VocabEditRow(en.Phrase)
                        {
                            Id = t.Id,
                            EnglishId = en.Id,
                            Token = t.Token,
                            ContextInfo = t.ContextInfo,
                            Representable = t.Representable,
                            OriginalPhrase = t.Phrase,
                            OriginalNote = t.Note ?? "",
                            Phrase = t.Phrase,
                            Note = t.Note ?? "",
                        };
                    })
                    .ToList();
            }

            if (version != _loadVersion)
                return; // a newer selection superseded this load — don't publish stale rows

            DetachRows(); // drop subscriptions from the previous language
            foreach (var r in newRows)
            {
                r.Revalidate(); // flag any already-invalid existing data (e.g. an empty phrase) at load
                r.PropertyChanged += OnRowChanged;
                r.ErrorsChanged += OnRowErrorsChanged;
            }

            _rows = newRows;
            _currentIso = iso;
            VocabGrid.ItemsSource = _rows;
            UpdateSaveState($"{_rows.Count} tokens for {iso}.");
            // WX-235: keep the Rerun QA button + in-flight read-only gate in sync with the loaded language.
            RerunButton.Iso = iso;
            _lastRerunStatus = App.QaRerunCoordinator.StatusFor(iso).Status;
            VocabGrid.IsReadOnly = App.QaRerunCoordinator.IsInFlight(iso);
        }
        catch (Exception ex)
        {
            if (version != _loadVersion)
                return;
            ClearRows(); // don't leave the previous language's rows editable behind a failed load
            StatusText.Text = "Error loading templates: " + ex.Message;
        }
    }

    private void DetachRows()
    {
        foreach (var old in _rows)
        {
            old.PropertyChanged -= OnRowChanged;
            old.ErrorsChanged -= OnRowErrorsChanged;
        }
    }

    private void ClearRows()
    {
        DetachRows();
        _rows = new List<VocabEditRow>();
        _currentIso = "";
        VocabGrid.ItemsSource = null;
        // No language loaded → the button has nothing to act on; clear its binding and the gate state so it
        // doesn't keep showing a prior language's status, and drop any in-flight lock the prior language held.
        RerunButton.Iso = "";
        _lastRerunStatus = null;
        VocabGrid.IsReadOnly = false;
        SaveButton.IsEnabled = false;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => UpdateSaveState();
    private void OnRowErrorsChanged(object? sender, DataErrorsChangedEventArgs e) => UpdateSaveState();

    // Save is enabled only when there is at least one change and no row has a validation error.
    private void UpdateSaveState(string? status = null)
    {
        var dirty = _rows.Count(r => r.IsDirty);
        var errors = _rows.Count(r => r.HasErrors);
        SaveButton.IsEnabled = dirty > 0 && errors == 0;

        if (status is not null)
            StatusText.Text = status;
        else if (errors > 0)
            StatusText.Text = $"{dirty} change(s), {errors} invalid — fix the highlighted cells to enable Save.";
        else if (dirty > 0)
            StatusText.Text = $"{dirty} change(s) ready to save.";
        else
            StatusText.Text = "No changes.";
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.QaRerunCoordinator.IsInFlight(_currentIso))
            return;   // a rerun is regenerating this language — don't write to the DB it's judging; the disabled button is the visual gate, this is the backstop
        VocabGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true); // flush the in-progress cell
        SaveButton.IsEnabled = false; // guard against a double-save while the write is in flight

        // Snapshot the values we are about to write: with UpdateSourceTrigger=PropertyChanged the operator
        // can keep typing during the await, so the baseline must be set to what was actually persisted (not
        // the possibly-newer current value) or a concurrent edit would be marked clean yet lost.
        var snapshot = _rows.Where(r => r.IsDirty && !r.HasErrors)
            .Select(r => (Row: r, r.Phrase, r.Note, r.English))
            .ToList();
        if (snapshot.Count == 0)
        {
            UpdateSaveState();
            return;
        }

        try
        {
            await using var ctx = new WeatherDataContext(App.DbOptions);
            var now = DateTime.UtcNow;
            const string reviewer = "WX-233 vocabulary editor";
            // Baseline only the rows actually written, with the note normalized to what was persisted
            // (whitespace-only is stored as NULL -> "") so a skipped/blank value isn't shown as saved-as-typed.
            var written = new List<(VocabEditRow Row, string Phrase, string RawNote, string PersistedNote, string English)>();
            foreach (var s in snapshot)
            {
                var persistedNote = VocabEditRow.NormalizeNote(s.Note);
                var baselineNote = VocabEditRow.NormalizeNote(s.Row.OriginalNote);
                var wrote = false;

                // The selected language's row: Phrase + Note — written (and re-stamped) only when one of them
                // actually changed, so an English-only edit does not re-stamp the target's review.
                if (!string.Equals(s.Phrase, s.Row.OriginalPhrase, StringComparison.Ordinal)
                    || !string.Equals(persistedNote, baselineNote, StringComparison.Ordinal))
                {
                    var tpl = await ctx.LanguageTemplates.FirstOrDefaultAsync(t => t.Id == s.Row.Id);
                    if (tpl is not null)
                    {
                        tpl.Phrase = s.Phrase;
                        tpl.Note = persistedNote.Length == 0 ? null : persistedNote;
                        tpl.ReviewedBy = reviewer;
                        tpl.ReviewedAtUtc = now;
                        wrote = true;
                    }
                }

                // WX-258: an edited English source writes the shared `en` row for this token, and stamps it.
                if (!string.Equals(s.English, s.Row.OriginalEnglish, StringComparison.Ordinal) && s.Row.EnglishId != 0)
                {
                    var enTpl = await ctx.LanguageTemplates.FirstOrDefaultAsync(t => t.Id == s.Row.EnglishId);
                    if (enTpl is not null)
                    {
                        enTpl.Phrase = s.English;
                        enTpl.ReviewedBy = reviewer;
                        enTpl.ReviewedAtUtc = now;
                        wrote = true;
                    }
                }

                // Only baseline a row we actually persisted. If the target/en row vanished under us (a
                // concurrent delete) or an English edit had no en row to write (EnglishId == 0), leave the row
                // DIRTY rather than clear it and falsely report it saved — restores the old `continue` guard.
                if (wrote)
                    written.Add((s.Row, s.Phrase, s.Note, persistedNote, s.English));
            }
            await ctx.SaveChangesAsync();
            foreach (var w in written)
            {
                // A whitespace-only note is persisted as "" — reflect that in the cell, unless the operator
                // kept editing during the save (then leave their newer value to stay dirty).
                if (string.Equals(w.Row.Note, w.RawNote, StringComparison.Ordinal))
                    w.Row.Note = w.PersistedNote;
                w.Row.SetBaseline(w.Phrase, w.PersistedNote, w.English); // baseline = what we persisted; a mid-save edit stays dirty
            }
            UpdateSaveState($"Saved {written.Count} change(s) at {DateTime.Now:HH:mm:ss}.");
        }
        catch (Exception ex)
        {
            UpdateSaveState();
            MessageBox.Show($"Save failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
    }

    // When the user clicks back into a Phrase cell that's currently flagged invalid, restore the pre-edit
    // value (the row still holds OriginalPhrase) — recovering from a bad edit is one click, not a retype.
    private void VocabGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        // Clicking back into a cell that's currently flagged invalid restores its pre-edit value —
        // recovering from a bad edit is one click, not a retype.
        if (e.Row.Item is not VocabEditRow row)
            return;
        if (e.Column == PhraseColumn && row.HasPhraseError)
            row.Phrase = row.OriginalPhrase;
        else if (e.Column == EnglishColumn && row.HasEnglishError)
            row.English = row.OriginalEnglish;
    }

    private sealed record LangItem(string Iso, string DisplayName, bool IsEnabled)
    {
        // WX-258/WX-261: a dormant (disabled) language keeps its curated rows and stays curatable, but is
        // marked so the operator can tell it apart from a live one at a glance.
        public string Display => IsEnabled ? $"{DisplayName} ({Iso})" : $"{DisplayName} ({Iso}) — disabled";
    }

    /// <summary>
    /// Editable row for one <c>LanguageTemplates</c> token. Phrase/Note are read-write; the rest display-only.
    /// Implements <see cref="INotifyDataErrorInfo"/> so each Phrase edit runs the validator chain live and the
    /// grid flags an invalid cell; adding a validator is appending to <see cref="PhraseValidators"/>.
    /// </summary>
    public sealed class VocabEditRow : INotifyPropertyChanged, INotifyDataErrorInfo
    {
        private readonly Dictionary<string, List<string>> _errors = new();

        public VocabEditRow(string english)
        {
            _english = english;
            OriginalEnglish = english;
        }

        public long Id { get; init; }
        public long EnglishId { get; init; }   // the en LanguageTemplates row an English edit writes (0 = no en row)
        public string Token { get; init; } = "";
        public string ContextInfo { get; init; } = "";
        public bool Representable { get; init; }

        public string OriginalPhrase { get; set; } = "";
        public string OriginalNote { get; set; } = "";
        public string OriginalEnglish { get; set; } = "";

        private string _english;
        // WX-258: the English source is editable in place. It also defines the {n} placeholder contract the
        // Phrase is validated against, so an English edit re-validates both itself and the Phrase.
        public string English
        {
            get => _english;
            set
            {
                if (_english == value)
                    return;
                _english = value;
                OnChanged(nameof(English));
                ValidateEnglish();
                ValidatePhrase();
            }
        }

        private string _phrase = "";
        public string Phrase
        {
            get => _phrase;
            set
            {
                if (_phrase == value)
                    return;
                _phrase = value;
                OnChanged(nameof(Phrase));
                ValidatePhrase();
            }
        }

        private string _note = "";
        public string Note
        {
            get => _note;
            set { if (_note != value) { _note = value; OnChanged(nameof(Note)); } }
        }

        // Normalize a note the same way the save does (a whitespace-only note persists as "").
        internal static string NormalizeNote(string s) => string.IsNullOrWhiteSpace(s) ? "" : s;

        // Note is compared under that normalization so a whitespace-only edit isn't seen as a change that
        // can never be saved (the save writes nothing for it, so it would otherwise stay dirty forever).
        public bool IsDirty =>
            !string.Equals(Phrase, OriginalPhrase, StringComparison.Ordinal) ||
            !string.Equals(NormalizeNote(Note), NormalizeNote(OriginalNote), StringComparison.Ordinal) ||
            !string.Equals(English, OriginalEnglish, StringComparison.Ordinal);

        /// <summary>Set the clean baseline to the values actually persisted (called after a successful save).</summary>
        public void SetBaseline(string phrase, string note, string english)
        {
            OriginalPhrase = phrase;
            OriginalNote = note;
            OriginalEnglish = english;
            OnChanged(nameof(IsDirty));
        }

        /// <summary>Run the validators once (used at load so already-invalid existing data is flagged).</summary>
        public void Revalidate()
        {
            ValidateEnglish();
            ValidatePhrase();
        }

        // The validator chain run against a candidate Phrase. Each returns an error message or null.
        // Append here to add a validator (Mirion pattern).
        private static readonly Func<VocabEditRow, string, string?>[] PhraseValidators =
        {
            (_, v) => string.IsNullOrWhiteSpace(v) ? "Phrase cannot be empty." : null,
            // Skip when the English source is absent — without the contract there is nothing to enforce
            // (and flagging a valid placeholder-bearing phrase would be wrong).
            (row, v) => string.IsNullOrEmpty(row.English) || TemplateValidation.PlaceholdersMatch(row.English, v)
                ? null
                : $"Phrase must keep the English source's {{n}} placeholders ({row.English}).",
        };

        private void ValidatePhrase()
        {
            var errs = PhraseValidators.Select(f => f(this, Phrase)).OfType<string>().ToList();
            if (errs.Count > 0)
                _errors[nameof(Phrase)] = errs;
            else
                _errors.Remove(nameof(Phrase));
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(Phrase)));
            OnChanged(nameof(HasPhraseError));
            OnChanged(nameof(PhraseErrorText));
        }

        /// <summary>Bindable flag for a persistent cell highlight (out of edit mode too).</summary>
        public bool HasPhraseError => _errors.ContainsKey(nameof(Phrase));

        /// <summary>Bindable joined error message for the cell tooltip in display mode.</summary>
        public string? PhraseErrorText =>
            _errors.TryGetValue(nameof(Phrase), out var e) ? string.Join("; ", e) : null;

        // WX-258: the English-source validator chain. English may be recased/reworded but must keep the
        // token's original {n} placeholders (every target phrase was validated against them).
        private static readonly Func<VocabEditRow, string, string?>[] EnglishValidators =
        {
            // A row with no en source row (EnglishId == 0) has a legitimately-empty, non-editable English —
            // don't flag it invalid, which would disable Save for the whole grid. Mirrors PhraseValidators.
            (row, v) => row.EnglishId != 0 && string.IsNullOrWhiteSpace(v) ? "English cannot be empty." : null,
            (row, v) => TemplateValidation.PlaceholdersMatch(row.OriginalEnglish, v)
                ? null
                : $"English edit must keep the original {{n}} placeholders ({row.OriginalEnglish}).",
        };

        private void ValidateEnglish()
        {
            var errs = EnglishValidators.Select(f => f(this, English)).OfType<string>().ToList();
            if (errs.Count > 0)
                _errors[nameof(English)] = errs;
            else
                _errors.Remove(nameof(English));
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(English)));
            OnChanged(nameof(HasEnglishError));
            OnChanged(nameof(EnglishErrorText));
        }

        /// <summary>Bindable flag for a persistent English-cell highlight (out of edit mode too).</summary>
        public bool HasEnglishError => _errors.ContainsKey(nameof(English));

        /// <summary>Bindable joined English error message for the cell tooltip in display mode.</summary>
        public string? EnglishErrorText =>
            _errors.TryGetValue(nameof(English), out var e) ? string.Join("; ", e) : null;

        // INotifyDataErrorInfo
        public bool HasErrors => _errors.Count > 0;
        public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
        public IEnumerable GetErrors(string? propertyName) =>
            propertyName is not null && _errors.TryGetValue(propertyName, out var e) ? e : Array.Empty<string>();

        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}