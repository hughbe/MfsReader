using System.Text;

namespace MfsReader.Utilities;

/// <summary>
/// Provides strict Mac OS Roman encoding for strings stored in MFS structures.
/// </summary>
internal static class MacRomanEncoding
{
    private static readonly Encoding Encoding = CreateEncoding();

    public static string GetString(ReadOnlySpan<byte> value) => Encoding.GetString(value);

    public static int GetBytes(ReadOnlySpan<char> value, Span<byte> destination)
    {
        if (value.Contains('\0'))
        {
            throw new ArgumentException("Value must not contain a null character.", nameof(value));
        }

        return Encoding.GetBytes(value, destination);
    }

    public static int GetByteCount(ReadOnlySpan<char> value)
    {
        if (value.Contains('\0'))
        {
            throw new ArgumentException("Value must not contain a null character.", nameof(value));
        }

        return Encoding.GetByteCount(value);
    }

    public static int GetChars(ReadOnlySpan<byte> value, Span<char> destination) =>
        Encoding.GetChars(value, destination);

    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return System.Text.Encoding.GetEncoding(
            10000,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }
}
