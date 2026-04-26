namespace TafParser;

/// <summary>
/// Thrown when a TAF string cannot be decoded because it is missing required
/// fields or contains an unrecoverable format error.
/// </summary>
public sealed class TafParseException : Exception
{
    /// <summary>Initializes a new instance with the specified error message.</summary>
    /// <param name="message">A message describing why parsing failed.</param>
    public TafParseException(string message) : base(message) { }
}