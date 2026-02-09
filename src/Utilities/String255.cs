using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MfsReader.Utilities;

/// <summary>
/// Represents a fixed-size string of up to 255 bytes (e.g., file names).
/// </summary>
[InlineArray(Size)]
public struct String255 : ISpanFormattable, IEquatable<String255>
{
    /// <summary>
    /// Gets the maximum size of the string in bytes.
    /// </summary>
    public const int Size = 255;

    /// <summary>
    /// The first element of the array.
    /// </summary>
    private byte _element0;

    /// <summary>
    /// Initializes a new instance of the <see cref="String255"/> struct.
    /// </summary>
    /// <param name="data">The span containing the string bytes.</param>
    /// <exception cref="ArgumentException">Thrown when the data span length is greater than <see cref="Size"/>.</exception>
    public String255(ReadOnlySpan<byte> data)
    {
        if (data.Length > Size)
        {
            throw new ArgumentException($"Data span must be at most {Size} bytes long.", nameof(data));
        }

        data.CopyTo(AsSpan());
        // Null-terminate if there's room (there always is since we check length <= 255 and size is 255)
        if (data.Length < Size)
        {
            AsSpan()[data.Length] = 0;
        }
    }

    /// <summary>
    /// Gets the length of the string (excluding null terminator).
    /// </summary>
    public readonly int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ReadOnlySpan<byte> span = AsReadOnlySpan();
            int length = span.IndexOf((byte)0);
            return length < 0 ? span.Length : length;
        }
    }

    /// <summary>
    /// Gets a span over the elements of the array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> AsSpan() =>
        MemoryMarshal.CreateSpan(ref _element0, Size);

    /// <summary>
    /// Gets a read-only span over the elements of the array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<byte> AsReadOnlySpan() =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _element0), Size);

    /// <summary>
    /// Attempts to format the string into the provided span without allocating.
    /// </summary>
    /// <param name="destination">The span to write the string to.</param>
    /// <param name="charsWritten">When this method returns, contains the number of characters written.</param>
    /// <returns>true if the formatting was successful; otherwise, false.</returns>
    public readonly bool TryFormat(Span<char> destination, out int charsWritten)
    {
        int length = Length;

        if (destination.Length < length)
        {
            charsWritten = 0;
            return false;
        }

        ReadOnlySpan<byte> span = AsReadOnlySpan();
        for (int i = 0; i < length; i++)
        {
            destination[i] = (char)span[i];
        }

        charsWritten = length;
        return true;
    }

    /// <inheritdoc/>
    readonly bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        TryFormat(destination, out charsWritten);

    /// <inheritdoc/>
    readonly string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        ReadOnlySpan<byte> span = AsReadOnlySpan();
        int length = Length;

        return Encoding.ASCII.GetString(span[..length]);
    }

    /// <summary>
    /// Determines whether this string equals the specified character span without allocating.
    /// </summary>
    /// <param name="other">The character span to compare with.</param>
    /// <returns>true if the strings are equal; otherwise, false.</returns>
    public readonly bool Equals(ReadOnlySpan<char> other)
    {
        int length = Length;
        if (other.Length != length)
        {
            return false;
        }

        ReadOnlySpan<byte> span = AsReadOnlySpan();
        for (int i = 0; i < length; i++)
        {
            if ((char)span[i] != other[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether this string equals the specified byte span (ASCII) without allocating.
    /// </summary>
    /// <param name="other">The byte span to compare with.</param>
    /// <returns>true if the strings are equal; otherwise, false.</returns>
    public readonly bool Equals(ReadOnlySpan<byte> other)
    {
        int length = Length;
        if (other.Length != length)
        {
            return false;
        }

        return AsReadOnlySpan()[..length].SequenceEqual(other);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(String255 other) =>
        AsReadOnlySpan().SequenceEqual(other.AsReadOnlySpan());

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) =>
        obj is String255 other && Equals(other);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override readonly int GetHashCode()
    {
        ReadOnlySpan<byte> span = AsReadOnlySpan();
        var hash = new HashCode();
        hash.AddBytes(span[..Length]);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Determines whether two <see cref="String255"/> instances are equal.
    /// </summary>
    public static bool operator ==(String255 left, String255 right) => left.Equals(right);

    /// <summary>
    /// Determines whether two <see cref="String255"/> instances are not equal.
    /// </summary>
    public static bool operator !=(String255 left, String255 right) => !left.Equals(right);

    /// <summary>
    /// Creates a <see cref="String255"/> from the specified character span.
    /// </summary>
    /// <param name="value">The character span to create from. Must be at most <see cref="Size"/> characters.</param>
    /// <returns>The new <see cref="String255"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown when the value is longer than <see cref="Size"/> characters.</exception>
    public static String255 FromString(ReadOnlySpan<char> value)
    {
        if (value.Length > Size)
        {
            throw new ArgumentException($"Value must be at most {Size} characters long.", nameof(value));
        }

        var result = new String255();
        var span = result.AsSpan();
        for (int i = 0; i < value.Length; i++)
        {
            span[i] = (byte)value[i];
        }

        return result;
    }

    /// <summary>
    /// Implicitly converts the <see cref="String255"/> to a <see cref="string"/>.
    /// </summary>
    /// <param name="str">The <see cref="String255"/> instance.</param>
    /// <returns>The converted string.</returns>
    public static implicit operator string(String255 str) => str.ToString();
}
