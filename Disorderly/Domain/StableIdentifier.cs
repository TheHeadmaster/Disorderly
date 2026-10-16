namespace Disorderly.Domain;

public static class StableIdentifier
{
    public static bool IsValid(string? identifier) => !string.IsNullOrEmpty(identifier)
        && char.IsAsciiLetterOrDigit(identifier[0])
        && identifier.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
}