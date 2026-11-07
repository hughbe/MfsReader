using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MfsReader.Utilities;

/// <summary>
/// Represents a fixed-size string of up to 4 bytes (e.g., file type, creator codes).
/// </summary>
[InlineArray(Size)]
public struct String4 : ISpanFormattable, IEquatable<String4>
{
    /// <summary>
    /// Gets the size of the string in bytes.
    /// </summary>
    public const int Size = 4;

    /// <summary>
    /// The first element of the array.
    /// </summary>
    private byte _element0;

    /// <summary>
    /// Initializes a new instance of the <see cref="String4"/> struct.
    /// </summary>
    /// <param name="data">The span containing the string bytes.</param>
    /// <exception cref="ArgumentException">Thrown when the data span length is not equal to <see cref="Size"/>.</exception>
    public String4(ReadOnlySpan<byte> data)
    {
        if (data.Length != Size)
        {
            throw new ArgumentException($"Data span must be exactly {Size} bytes long.", nameof(data));
        }

        data.CopyTo(AsSpan());
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
        ReadOnlySpan<byte> span = AsReadOnlySpan();
        int length = Length;

        if (destination.Length < length)
        {
            charsWritten = 0;
            return false;
        }

        charsWritten = MacRomanEncoding.GetChars(span[..length], destination);
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

        return MacRomanEncoding.GetString(span[..length]);
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

        Span<byte> encoded = stackalloc byte[Size];
        try
        {
            MacRomanEncoding.GetBytes(other, encoded);
            return AsReadOnlySpan()[..length].SequenceEqual(encoded[..length]);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
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
    public readonly bool Equals(String4 other) =>
        Unsafe.As<String4, uint>(ref Unsafe.AsRef(in this)) == Unsafe.As<String4, uint>(ref Unsafe.AsRef(in other));

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) =>
        obj is String4 other && Equals(other);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override readonly int GetHashCode() =>
        Unsafe.As<String4, int>(ref Unsafe.AsRef(in this));

    /// <summary>
    /// Determines whether two <see cref="String4"/> instances are equal.
    /// </summary>
    public static bool operator ==(String4 left, String4 right) => left.Equals(right);

    /// <summary>
    /// Determines whether two <see cref="String4"/> instances are not equal.
    /// </summary>
    public static bool operator !=(String4 left, String4 right) => !left.Equals(right);

    /// <summary>
    /// Creates a <see cref="String4"/> from the specified character span.
    /// </summary>
    /// <param name="value">The character span to create from. Must be at most <see cref="Size"/> characters.</param>
    /// <returns>The new <see cref="String4"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown when the value is longer than <see cref="Size"/> characters.</exception>
    public static String4 FromString(ReadOnlySpan<char> value)
    {
        int byteCount = MacRomanEncoding.GetByteCount(value);
        if (byteCount > Size)
        {
            throw new ArgumentException($"Value must be at most {Size} bytes when encoded as Mac OS Roman.", nameof(value));
        }

        var result = new String4();
        MacRomanEncoding.GetBytes(value, result.AsSpan());

        return result;
    }

    /// <summary>
    /// Implicitly converts the <see cref="String4"/> to a <see cref="string"/>.
    /// </summary>
    /// <param name="str">The <see cref="String4"/> instance.</param>
    /// <returns>The converted string.</returns>
    public static implicit operator string(String4 str) => str.ToString();
}
