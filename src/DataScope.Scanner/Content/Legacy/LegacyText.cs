using System.Text;

namespace DataScope.Scanner.Content.Legacy;

/// <summary>Character sets the older Office formats store 8-bit text in.</summary>
internal static class LegacyText
{
    /// <summary>Windows' Western European code page, which "compressed" text in Word and Excel files uses.</summary>
    public static Encoding Windows1252 { get; } = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
}
