using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MfsReader;

/// <summary>
/// Represents an MFS timestamp, stored as seconds since the MacOS epoch (January 1, 1904 00:00:00 UTC).
/// </summary>
public readonly struct MfsTimestamp : IEquatable<MfsTimestamp>, IComparable<MfsTimestamp>, IFormattable
{
    /// <summary>
    /// The size of an MFS timestamp in bytes.
    /// </summary>
    public const int Size = 4;

    /// <summary>
    /// The MacOS epoch (January 1, 1904 00:00:00 UTC).
    /// </summary>
    private static readonly DateTime MacOSEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Gets the raw timestamp value (seconds since the MacOS epoch).
    /// </summary>
    public uint Value { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MfsTimestamp"/> struct from the given data.
    /// </summary>
    /// <param name="data">The span containing the big-endian 4-byte timestamp.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MfsTimestamp(ReadOnlySpan<byte> data)
    {
        Debug.Assert(data.Length >= Size, "Data span must contain at least 4 bytes for the timestamp.");

        Value = BinaryPrimitives.ReadUInt32BigEndian(data);
    }

    /// <summary>
    /// Converts the MFS timestamp to a <see cref="DateTime"/>.
    /// </summary>
    /// <returns>The corresponding <see cref="DateTime"/> value in UTC.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DateTime ToDateTime() => MacOSEpoch.AddSeconds(Value);

    /// <summary>
    /// Converts the <see cref="MfsTimestamp"/> to a <see cref="DateTime"/>.
    /// </summary>
    /// <param name="timestamp">The <see cref="MfsTimestamp"/> instance.</param>
    /// <returns>The corresponding <see cref="DateTime"/> value.</returns>
    public static implicit operator DateTime(MfsTimestamp timestamp) => timestamp.ToDateTime();

    /// <inheritdoc/>
    public bool Equals(MfsTimestamp other) => Value == other.Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MfsTimestamp other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value.GetHashCode();

    /// <inheritdoc/>
    public int CompareTo(MfsTimestamp other) => Value.CompareTo(other.Value);

    /// <summary>
    /// Determines whether two <see cref="MfsTimestamp"/> instances are equal.
    /// </summary>
    public static bool operator ==(MfsTimestamp left, MfsTimestamp right) => left.Equals(right);

    /// <summary>
    /// Determines whether two <see cref="MfsTimestamp"/> instances are not equal.
    /// </summary>
    public static bool operator !=(MfsTimestamp left, MfsTimestamp right) => !left.Equals(right);

    /// <summary>
    /// Formats the timestamp using the specified format string.
    /// </summary>
    /// <param name="format">A standard or custom date and time format string.</param>
    /// <returns>A string representation of the timestamp.</returns>
    public string ToString(string format) => ToDateTime().ToString(format);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToDateTime().ToString(format, formatProvider);

    /// <inheritdoc/>
    public override string ToString() => ToDateTime().ToString("O");
}
