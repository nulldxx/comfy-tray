using System;
using System.Text;

namespace ComfyTray;

/// <summary>
/// Base64 (UTF-8) codec used for the values ComfyTray stores in the registry, so command
/// lines with quotes, arguments and non-ASCII paths survive a round trip untouched by
/// registry tooling.
/// </summary>
internal static class Base64Text
{
    public static string Encode(string? value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    /// <summary>Decodes a value written by <see cref="Encode"/>; malformed input decodes to empty.</summary>
    public static string Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
