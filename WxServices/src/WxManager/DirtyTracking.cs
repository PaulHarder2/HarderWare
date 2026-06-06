using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace WxManager;

/// <summary>
/// Shared change-listener wiring for WxManager's dirty-tracking (WX-134):
/// Save-like buttons stay disabled until something has changed and is eligible
/// to be saved, and return to disabled after a successful save. Each tab calls
/// <see cref="Attach"/> once (at the end of its constructor, after initial
/// defaults are set) with its editable controls and a callback that enables its
/// Save button — guarded by the tab's own suppression flag so programmatic
/// loads don't count as edits.
/// </summary>
internal static class DirtyTracking
{
    /// <summary>
    /// Attaches the appropriate change event of each control to
    /// <paramref name="onChange"/>: <c>TextChanged</c> for TextBoxes,
    /// <c>PasswordChanged</c> for PasswordBoxes, and both
    /// <c>SelectionChanged</c> and the routed <c>TextChanged</c> (for the
    /// editable-text case) for ComboBoxes. Unknown control types are ignored.
    /// </summary>
    /// <param name="onChange">Invoked on every user-visible change.</param>
    /// <param name="controls">The editable controls to watch.</param>
    public static void Attach(Action onChange, params Control[] controls)
    {
        foreach (var control in controls)
        {
            switch (control)
            {
                case PasswordBox passwordBox:
                    passwordBox.PasswordChanged += (_, _) => onChange();
                    break;
                case TextBox textBox:
                    textBox.TextChanged += (_, _) => onChange();
                    break;
                case ComboBox comboBox:
                    comboBox.SelectionChanged += (_, _) => onChange();
                    comboBox.AddHandler(TextBoxBase.TextChangedEvent,
                        new TextChangedEventHandler((_, _) => onChange()));
                    break;
            }
        }
    }
}