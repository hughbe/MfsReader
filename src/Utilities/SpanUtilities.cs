using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace MfsReader.Utilities;

/// <summary>
/// Provides utility methods for reading data from spans.
/// </summary>
internal static class SpanUtilities
{
    /// <summary>
    /// Reads a fixed-length ASCII string with a length prefix from the specified span.
    /// </summary>
    /// <param name="data">The span containing the data.</param>
    /// <returns>The decoded string.</returns>
    public static string ReadPascalString(ReadOnlySpan<byte> data)
    {
        Debug.Assert(data.Length > 0, "Data span must contain at least one byte for the length.");

        // There is one extra byte for the length.
        var actualLength = data[0];

        // Read the string bytes for the fixed length.
        return Encoding.ASCII.GetString(data.Slice(1, Math.Min(actualLength, data.Length - 1)));
    }

    /// <summary>
    /// Reads an HFS timestamp from the specified span and converts it to a <see cref="DateTime"/>.
    /// </summary>
    /// <param name="data">The span containing the data.</param>
    /// <returns>The corresponding <see cref="DateTime"/> value.</returns>
    public static DateTime ReadMacOSTimestamp(ReadOnlySpan<byte> data)
    {
        Debug.Assert(data.Length >= 4, "Data span must contain at least 4 bytes for the timestamp.");

        // 4 bytes MacOS timestamp
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(data);

        // MacOS timestamps are seconds since 00:00:00 on January 1, 1904
        var epoch = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return epoch.AddSeconds(timestamp);
    }
}
