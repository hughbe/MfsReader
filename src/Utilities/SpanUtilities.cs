using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Microsoft.VisualBasic;

namespace MfsReader.Utilities;

/// <summary>
/// Provides utility methods for reading data from spans.
/// </summary>
internal static class SpanUtilities
{
    /// <summary>
    /// Reads a Pascal-style string from the given span.
    /// </summary>
    /// <param name="data">The span containing the Pascal string.</param>
    /// <param name="bytesRead">Outputs the total number of bytes read from the span.</param>
    /// <returns>The extracted string.</returns>
    /// <exception cref="ArgumentException">Thrown when the data is too short to contain the specified Pascal string.</exception>
    public static string ReadPascalString(ReadOnlySpan<byte> data, out int bytesRead)
    {
        if (data.Length < 1)
        {
            throw new ArgumentException("Data is too short to contain a Pascal string length.", nameof(data));
        }

        var strLength = data[0];
        if (1 + strLength > data.Length)
        {
            throw new ArgumentException("Data is too short to contain the specified Pascal string.", nameof(data));
        }

        bytesRead = 1 + strLength;
        return Encoding.ASCII.GetString(data.Slice(1, strLength));
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
