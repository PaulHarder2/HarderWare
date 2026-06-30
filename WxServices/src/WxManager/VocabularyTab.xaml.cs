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
/// edit each token's <c>Phrase</c> and <c>Note</c> in a grid, and Save. Each Phrase edit runs through a
/// chain of validators live (Mirion pattern): the cell flags invalid input and the Save button stays
/// disabled until every change is valid. Token, English source, Context, and Representable are read-only
/// (cross-language attributes — changed via migration, not here). English itself is excluded from the
/// selector: it is the source, not an editable target.
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
        // Lock the grid while this language is regenerating (any edit would immediately re-stale the package).
        VocabGrid.IsReadOnly = v.Status == QaRerunStatus.Running;
        // Reload-from-DB on press: when the rerun begins, snap the grid to the committed DB state — dropping any
        // unsaved edits — so what the service judges is exactly what is shown.
        if (v.Status == QaRerunStatus.Running && _lastRerunStatus != QaRerunStatus.Running)
            _ = LoadTemplatesAsync(iso);
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
            // English is the cross-language source, not an editable target — excluded from the selector.
            // Changing an English phrase would force re-reviewing every target translation, so it is out
            // of scope here (fix the baseline via a migration). English still shows as the read-only
            // "English (source)" reference column for each target.
            var langs = await ctx.Languages
                .Where(l => l.IsoCode != "en" && ctx.LanguageTemplates.Any(t => t.LanguageId == l.Id))
                .OrderBy(l => l.IsoCode)
                .Select(l => new LangItem(l.IsoCode, l.DisplayName))
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
                    .ToDictionaryAsync(t => t.Token, t => t.Phrase);

                newRows = (await ctx.LanguageTemplates.Include(t => t.Language)
                        .Where(t => t.Language!.IsoCode == iso)
                        .OrderBy(t => t.Token)
                        .AsNoTracking()
                        .ToListAsync())
                    .Select(t => new VocabEditRow(enSource.GetValueOrDefault(t.Token, ""))
                    {
                        Id = t.Id,
                        Token = t.Token,
                        ContextInfo = t.ContextInfo,
                        Representable = t.Representable,
                        OriginalPhrase = t.Phrase,
                        OriginalNote = t.Note ?? "",
                        Phrase = t.Phrase,
                        Note = t.Note ?? "",
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
        VocabGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true); // flush the in-progress cell
        SaveButton.IsEnabled = false; // guard against a double-save while the write is in flight

        // Snapshot the values we are about to write: with UpdateSourceTrigger=PropertyChanged the operator
        // can keep typing during the await, so the baseline must be set to what was actually persisted (not
        // the possibly-newer current value) or a concurrent edit would be marked clean yet lost.
        var snapshot = _rows.Where(r => r.IsDirty && !r.HasErrors)
            .Select(r => (Row: r, r.Phrase, r.Note))
            .ToList();
        if (snapshot.Count == 0)
        {
            UpdateSaveState();
            return;
        }

        try
        {
            await using var ctx = new WeatherDataContext(App.DbOptions);
            // Baseline only the rows actually written, with the note normalized to what was persisted
            // (whitespace-only is stored as NULL -> "") so a skipped/blank value isn't shown as saved-as-typed.
            var written = new List<(VocabEditRow Row, string Phrase, string RawNote, string PersistedNote)>();
            foreach (var s in snapshot)
            {
                var tpl = await ctx.LanguageTemplates.FirstOrDefaultAsync(t => t.Id == s.Row.Id);
                if (tpl is null)
                    continue;
                var persistedNote = string.IsNullOrWhiteSpace(s.Note) ? "" : s.Note;
                tpl.Phrase = s.Phrase;
                tpl.Note = persistedNote.Length == 0 ? null : persistedNote;
                tpl.ReviewedBy = "WX-233 vocabulary editor";
                tpl.ReviewedAtUtc = DateTime.UtcNow;
                written.Add((s.Row, s.Phrase, s.Note, persistedNote));
            }
            await ctx.SaveChangesAsync();
            foreach (var w in written)
            {
                // A whitespace-only note is persisted as "" — reflect that in the cell, unless the operator
                // kept editing during the save (then leave their newer value to stay dirty).
                if (string.Equals(w.Row.Note, w.RawNote, StringComparison.Ordinal))
                    w.Row.Note = w.PersistedNote;
                w.Row.SetBaseline(w.Phrase, w.PersistedNote); // baseline = what we persisted; a mid-save edit stays dirty
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
        if (e.Column == PhraseColumn && e.Row.Item is VocabEditRow row && row.HasPhraseError)
            row.Phrase = row.OriginalPhrase;
    }

    private sealed record LangItem(string Iso, string DisplayName)
    {
        public string Display => $"{DisplayName} ({Iso})";
    }

    /// <summary>
    /// Editable row for one <c>LanguageTemplates</c> token. Phrase/Note are read-write; the rest display-only.
    /// Implements <see cref="INotifyDataErrorInfo"/> so each Phrase edit runs the validator chain live and the
    /// grid flags an invalid cell; adding a validator is appending to <see cref="PhraseValidators"/>.
    /// </summary>
    public sealed class VocabEditRow : INotifyPropertyChanged, INotifyDataErrorInfo
    {
        private readonly Dictionary<string, List<string>> _errors = new();

        public VocabEditRow(string english) => English = english;

        public long Id { get; init; }
        public string Token { get; init; } = "";
        public string English { get; }
        public string ContextInfo { get; init; } = "";
        public bool Representable { get; init; }

        public string OriginalPhrase { get; set; } = "";
        public string OriginalNote { get; set; } = "";

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

        public bool IsDirty =>
            !string.Equals(Phrase, OriginalPhrase, StringComparison.Ordinal) ||
            !string.Equals(Note, OriginalNote, StringComparison.Ordinal);

        /// <summary>Set the clean baseline to the values actually persisted (called after a successful save).</summary>
        public void SetBaseline(string phrase, string note)
        {
            OriginalPhrase = phrase;
            OriginalNote = note;
            OnChanged(nameof(IsDirty));
        }

        /// <summary>Run the validators once (used at load so already-invalid existing data is flagged).</summary>
        public void Revalidate() => ValidatePhrase();

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