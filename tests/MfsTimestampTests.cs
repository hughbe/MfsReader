namespace MfsReader.Tests;

public class MfsTimestampTests
{
    [Fact]
    public void Ctor_FromSpan_ReadsValue()
    {
        byte[] data = [0x00, 0x01, 0x51, 0x80]; // 86400 in big-endian
        var ts = new MfsTimestamp(data);
        Assert.Equal(86400u, ts.Value);
    }

    [Fact]
    public void Ctor_FromValue_SetsValue()
    {
        var ts = new MfsTimestamp(12345);
        Assert.Equal(12345u, ts.Value);
    }

    [Fact]
    public void ToDateTime_ReturnsCorrectDate()
    {
        // 86400 seconds = 1 day after Jan 1, 1904
        var ts = new MfsTimestamp(86400);
        var expected = new DateTime(1904, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, ts.ToDateTime());
    }

    [Fact]
    public void ToDateTime_Zero_ReturnsMacEpoch()
    {
        var ts = new MfsTimestamp(0);
        var expected = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, ts.ToDateTime());
    }

    [Fact]
    public void FromDateTime_Roundtrips()
    {
        var dt = new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var ts = MfsTimestamp.FromDateTime(dt);
        var roundtripped = ts.ToDateTime();
        Assert.Equal(dt, roundtripped);
    }

    [Fact]
    public void ImplicitOperator_DateTime_ConvertsCorrectly()
    {
        var ts = new MfsTimestamp(86400);
        DateTime dt = ts;
        Assert.Equal(new DateTime(1904, 1, 2, 0, 0, 0, DateTimeKind.Utc), dt);
    }

    [Fact]
    public void WriteTo_WritesBigEndian()
    {
        var ts = new MfsTimestamp(86400);
        Span<byte> data = stackalloc byte[4];
        ts.WriteTo(data);
        Assert.Equal((byte)0x00, data[0]);
        Assert.Equal((byte)0x01, data[1]);
        Assert.Equal((byte)0x51, data[2]);
        Assert.Equal((byte)0x80, data[3]);
    }

    [Fact]
    public void Equals_SameValue_ReturnsTrue()
    {
        var a = new MfsTimestamp(100);
        var b = new MfsTimestamp(100);
        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a != b);
    }

    [Fact]
    public void Equals_DifferentValue_ReturnsFalse()
    {
        var a = new MfsTimestamp(100);
        var b = new MfsTimestamp(200);
        Assert.False(a.Equals(b));
        Assert.False(a == b);
        Assert.True(a != b);
    }

    [Fact]
    public void Equals_Object_WorksCorrectly()
    {
        var ts = new MfsTimestamp(100);
        Assert.True(ts.Equals((object)new MfsTimestamp(100)));
        Assert.False(ts.Equals((object)new MfsTimestamp(200)));
        Assert.False(ts.Equals("not a timestamp"));
    }

    [Fact]
    public void GetHashCode_SameValue_SameHash()
    {
        var a = new MfsTimestamp(100);
        var b = new MfsTimestamp(100);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void CompareTo_OrdersCorrectly()
    {
        var earlier = new MfsTimestamp(100);
        var later = new MfsTimestamp(200);
        Assert.True(earlier.CompareTo(later) < 0);
        Assert.True(later.CompareTo(earlier) > 0);
        Assert.Equal(0, earlier.CompareTo(new MfsTimestamp(100)));
    }

    [Fact]
    public void ToString_ReturnsIsoFormat()
    {
        var ts = new MfsTimestamp(0);
        string result = ts.ToString();
        Assert.Contains("1904", result);
    }

    [Fact]
    public void ToString_WithFormat_Formats()
    {
        var ts = new MfsTimestamp(0);
        string result = ts.ToString("yyyy");
        Assert.Equal("1904", result);
    }

    [Fact]
    public void ToString_WithFormatAndProvider_Formats()
    {
        var ts = new MfsTimestamp(0);
        string result = ts.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("1904", result);
    }
}
