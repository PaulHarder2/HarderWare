namespace WxReport.Svc;

/// <summary>
/// Wraps the contents of the repo-root <c>AboutPaul.md</c> file, loaded once at
/// service startup and supplied to every <see cref="ClaudeClient"/> so it can
/// be injected as a cached system-prompt prefix on Anthropic Messages API calls.
/// </summary>
/// <param name="Text">Full text of <c>AboutPaul.md</c> as read from disk.</param>
public sealed record PersonaPrefix(string Text);