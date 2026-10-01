using System.Text;

namespace Migrator.Core.Migration;

public static class TextFiles
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Lazy<Encoding> Windows1252 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    });

    /// <summary>Reads a source file honoring BOMs and falling back to Windows-1252 for legacy ANSI files.</summary>
    public static (string Text, bool WasAnsi) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), false);
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))
        {
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            return (reader.ReadToEnd(), false);
        }

        try
        {
            return (StrictUtf8.GetString(bytes), false);
        }
        catch (DecoderFallbackException)
        {
            return (Windows1252.Value.GetString(bytes), true);
        }
    }
}
