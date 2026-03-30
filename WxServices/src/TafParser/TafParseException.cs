namespace TafParser;

/// <summary>
/// Thrown when a TAF string cannot be decoded because it is missing required
/// fields or contains an unrecoverable format error.
/// </summary>
public sealed class TafParseException : Exception
{
    public TafParseException(string message) : base(message) { }
}
