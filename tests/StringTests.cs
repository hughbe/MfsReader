using MfsReader.Utilities;

namespace MfsReader.Tests;

public class String4Tests
{
    [Fact]
    public void Ctor_ValidData_SetsBytes()
    {
        var s = new String4("TEXT"u8);
        Assert.Equal("TEXT", s.ToString());
    }

    [Fact]
    public void Ctor_WrongSize_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new String4("TE"u8));
        Assert.Throws<ArgumentException>(() => new String4("TOOLONG"u8));
    }

    [Fact]
    public void Length_NoNulls_ReturnsFull()
    {
        var s = String4.FromString("TEXT");
        Assert.Equal(4, s.Length);
    }

    [Fact]
    public void Length_WithNulls_ReturnsUpToNull()
    {
        var s = new String4([0x41, 0x42, 0x00, 0x00]);
        Assert.Equal(2, s.Length);
    }

    [Fact]
    public void FromString_Valid_Roundtrips()
    {
        var s = String4.FromString("ABCD");
        Assert.Equal("ABCD", s.ToString());
    }

    [Fact]
    public void FromString_TooLong_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => String4.FromString("TOOLONG"));
    }

    [Fact]
    public void TryFormat_SufficientSpace_WritesChars()
    {
        var s = String4.FromString("TEXT");
        Span<char> dest = stackalloc char[10];
        Assert.True(s.TryFormat(dest, out int written));
        Assert.Equal(4, written);
        Assert.Equal("TEXT", new string(dest[..written]));
    }

    [Fact]
    public void TryFormat_InsufficientSpace_ReturnsFalse()
    {
        var s = String4.FromString("TEXT");
        Span<char> dest = stackalloc char[2];
        Assert.False(s.TryFormat(dest, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void ISpanFormattable_TryFormat_Delegates()
    {
        var s = String4.FromString("TEXT");
        ISpanFormattable formattable = s;
        Span<char> dest = stackalloc char[10];
        Assert.True(formattable.TryFormat(dest, out int written, default, null));
        Assert.Equal(4, written);
    }

    [Fact]
    public void IFormattable_ToString_ReturnsString()
    {
        var s = String4.FromString("TEXT");
        IFormattable formattable = s;
        Assert.Equal("TEXT", formattable.ToString(null, null));
    }

    [Fact]
    public void ImplicitOperator_String_Converts()
    {
        var s = String4.FromString("TEXT");
        string str = s;
        Assert.Equal("TEXT", str);
    }

    [Fact]
    public void Equals_CharSpan_MatchReturnsTrue()
    {
        var s = String4.FromString("TEXT");
        Assert.True(s.Equals("TEXT".AsSpan()));
    }

    [Fact]
    public void Equals_CharSpan_MismatchReturnsFalse()
    {
        var s = String4.FromString("TEXT");
        Assert.False(s.Equals("NOPE".AsSpan()));
        Assert.False(s.Equals("TE".AsSpan()));
    }

    [Fact]
    public void Equals_ByteSpan_MatchReturnsTrue()
    {
        var s = String4.FromString("TEXT");
        Assert.True(s.Equals("TEXT"u8));
    }

    [Fact]
    public void Equals_ByteSpan_MismatchReturnsFalse()
    {
        var s = String4.FromString("TEXT");
        Assert.False(s.Equals("NOPE"u8));
        Assert.False(s.Equals("TE"u8));
    }

    [Fact]
    public void Equals_String4_SameValue_ReturnsTrue()
    {
        var a = String4.FromString("TEXT");
        var b = String4.FromString("TEXT");
        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a != b);
    }

    [Fact]
    public void Equals_String4_DifferentValue_ReturnsFalse()
    {
        var a = String4.FromString("TEXT");
        var b = String4.FromString("APPL");
        Assert.False(a.Equals(b));
        Assert.False(a == b);
        Assert.True(a != b);
    }

    [Fact]
    public void Equals_Object_WorksCorrectly()
    {
        var s = String4.FromString("TEXT");
        Assert.True(s.Equals((object)String4.FromString("TEXT")));
        Assert.False(s.Equals((object)String4.FromString("APPL")));
        Assert.False(s.Equals((object)"TEXT"));
    }

    [Fact]
    public void GetHashCode_SameValue_SameHash()
    {
        var a = String4.FromString("TEXT");
        var b = String4.FromString("TEXT");
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}

public class String27Tests
{
    [Fact]
    public void Ctor_ValidData_SetsBytes()
    {
        var s = new String27("TestVol"u8);
        Assert.Equal("TestVol", s.ToString());
    }

    [Fact]
    public void Ctor_TooLong_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new String27(new byte[28]));
    }

    [Fact]
    public void Ctor_MaxLength_DoesNotThrow()
    {
        var data = new byte[27];
        Array.Fill(data, (byte)'A');
        var s = new String27(data);
        Assert.Equal(27, s.Length);
    }

    [Fact]
    public void Length_WithNulls_ReturnsUpToNull()
    {
        var s = new String27([0x41, 0x42, 0x00]);
        Assert.Equal(2, s.Length);
    }

    [Fact]
    public void FromString_Valid_Roundtrips()
    {
        var s = String27.FromString("MyVolume");
        Assert.Equal("MyVolume", s.ToString());
    }

    [Fact]
    public void FromString_TooLong_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => String27.FromString(new string('A', 28)));
    }

    [Fact]
    public void TryFormat_SufficientSpace_WritesChars()
    {
        var s = String27.FromString("Test");
        Span<char> dest = stackalloc char[10];
        Assert.True(s.TryFormat(dest, out int written));
        Assert.Equal(4, written);
        Assert.Equal("Test", new string(dest[..written]));
    }

    [Fact]
    public void TryFormat_InsufficientSpace_ReturnsFalse()
    {
        var s = String27.FromString("TestVolume");
        Span<char> dest = stackalloc char[2];
        Assert.False(s.TryFormat(dest, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void ISpanFormattable_TryFormat_Delegates()
    {
        var s = String27.FromString("Test");
        ISpanFormattable formattable = s;
        Span<char> dest = stackalloc char[10];
        Assert.True(formattable.TryFormat(dest, out int written, default, null));
        Assert.Equal(4, written);
    }

    [Fact]
    public void IFormattable_ToString_ReturnsString()
    {
        var s = String27.FromString("Test");
        IFormattable formattable = s;
        Assert.Equal("Test", formattable.ToString(null, null));
    }

    [Fact]
    public void ImplicitOperator_String_Converts()
    {
        var s = String27.FromString("Test");
        string str = s;
        Assert.Equal("Test", str);
    }

    [Fact]
    public void Equals_CharSpan_MatchReturnsTrue()
    {
        var s = String27.FromString("Test");
        Assert.True(s.Equals("Test".AsSpan()));
    }

    [Fact]
    public void Equals_CharSpan_MismatchReturnsFalse()
    {
        var s = String27.FromString("Test");
        Assert.False(s.Equals("Nope".AsSpan()));
        Assert.False(s.Equals("Te".AsSpan()));
    }

    [Fact]
    public void Equals_CharSpan_CharMismatchReturnsFalse()
    {
        var s = String27.FromString("ABCD");
        Assert.False(s.Equals("ABCE".AsSpan()));
    }

    [Fact]
    public void Equals_ByteSpan_MatchReturnsTrue()
    {
        var s = String27.FromString("Test");
        Assert.True(s.Equals("Test"u8));
    }

    [Fact]
    public void Equals_ByteSpan_MismatchReturnsFalse()
    {
        var s = String27.FromString("Test");
        Assert.False(s.Equals("Nope"u8));
        Assert.False(s.Equals("Te"u8));
    }

    [Fact]
    public void Equals_String27_SameValue_ReturnsTrue()
    {
        var a = String27.FromString("Test");
        var b = String27.FromString("Test");
        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a != b);
    }

    [Fact]
    public void Equals_String27_DifferentValue_ReturnsFalse()
    {
        var a = String27.FromString("Test");
        var b = String27.FromString("Other");
        Assert.False(a.Equals(b));
        Assert.False(a == b);
        Assert.True(a != b);
    }

    [Fact]
    public void Equals_Object_WorksCorrectly()
    {
        var s = String27.FromString("Test");
        Assert.True(s.Equals((object)String27.FromString("Test")));
        Assert.False(s.Equals((object)String27.FromString("Other")));
        Assert.False(s.Equals((object)"Test"));
    }

    [Fact]
    public void GetHashCode_SameValue_SameHash()
    {
        var a = String27.FromString("Test");
        var b = String27.FromString("Test");
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}

public class String255Tests
{
    [Fact]
    public void Ctor_ValidData_SetsBytes()
    {
        var s = new String255("Hello"u8);
        Assert.Equal("Hello", s.ToString());
    }

    [Fact]
    public void Ctor_TooLong_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new String255(new byte[256]));
    }

    [Fact]
    public void Ctor_MaxLength_DoesNotThrow()
    {
        var data = new byte[255];
        for (int i = 0; i < 255; i++) data[i] = (byte)'A';
        var s = new String255(data);
        Assert.Equal(255, s.Length);
    }

    [Fact]
    public void Ctor_NullTerminates_WhenRoom()
    {
        var s = new String255("Hi"u8);
        Assert.Equal(2, s.Length);
        Assert.Equal("Hi", s.ToString());
    }

    [Fact]
    public void Length_WithNulls_ReturnsUpToNull()
    {
        var s = new String255([0x41, 0x42, 0x00]);
        Assert.Equal(2, s.Length);
    }

    [Fact]
    public void FromString_Valid_Roundtrips()
    {
        var s = String255.FromString("MyFile.txt");
        Assert.Equal("MyFile.txt", s.ToString());
    }

    [Fact]
    public void FromString_TooLong_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => String255.FromString(new string('A', 256)));
    }

    [Fact]
    public void TryFormat_SufficientSpace_WritesChars()
    {
        var s = String255.FromString("Hello");
        Span<char> dest = stackalloc char[10];
        Assert.True(s.TryFormat(dest, out int written));
        Assert.Equal(5, written);
        Assert.Equal("Hello", new string(dest[..written]));
    }

    [Fact]
    public void TryFormat_InsufficientSpace_ReturnsFalse()
    {
        var s = String255.FromString("Hello");
        Span<char> dest = stackalloc char[2];
        Assert.False(s.TryFormat(dest, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void ISpanFormattable_TryFormat_Delegates()
    {
        var s = String255.FromString("Hello");
        ISpanFormattable formattable = s;
        Span<char> dest = stackalloc char[10];
        Assert.True(formattable.TryFormat(dest, out int written, default, null));
        Assert.Equal(5, written);
    }

    [Fact]
    public void IFormattable_ToString_ReturnsString()
    {
        var s = String255.FromString("Hello");
        IFormattable formattable = s;
        Assert.Equal("Hello", formattable.ToString(null, null));
    }

    [Fact]
    public void ImplicitOperator_String_Converts()
    {
        var s = String255.FromString("Hello");
        string str = s;
        Assert.Equal("Hello", str);
    }

    [Fact]
    public void Equals_CharSpan_MatchReturnsTrue()
    {
        var s = String255.FromString("Hello");
        Assert.True(s.Equals("Hello".AsSpan()));
    }

    [Fact]
    public void Equals_CharSpan_MismatchReturnsFalse()
    {
        var s = String255.FromString("Hello");
        Assert.False(s.Equals("World".AsSpan()));
        Assert.False(s.Equals("He".AsSpan()));
    }

    [Fact]
    public void Equals_CharSpan_CharMismatchReturnsFalse()
    {
        var s = String255.FromString("ABCD");
        Assert.False(s.Equals("ABCE".AsSpan()));
    }

    [Fact]
    public void Equals_ByteSpan_MatchReturnsTrue()
    {
        var s = String255.FromString("Hello");
        Assert.True(s.Equals("Hello"u8));
    }

    [Fact]
    public void Equals_ByteSpan_MismatchReturnsFalse()
    {
        var s = String255.FromString("Hello");
        Assert.False(s.Equals("World"u8));
        Assert.False(s.Equals("He"u8));
    }

    [Fact]
    public void Equals_String255_SameValue_ReturnsTrue()
    {
        var a = String255.FromString("Hello");
        var b = String255.FromString("Hello");
        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a != b);
    }

    [Fact]
    public void Equals_String255_DifferentValue_ReturnsFalse()
    {
        var a = String255.FromString("Hello");
        var b = String255.FromString("World");
        Assert.False(a.Equals(b));
        Assert.False(a == b);
        Assert.True(a != b);
    }

    [Fact]
    public void Equals_Object_WorksCorrectly()
    {
        var s = String255.FromString("Hello");
        Assert.True(s.Equals((object)String255.FromString("Hello")));
        Assert.False(s.Equals((object)String255.FromString("World")));
        Assert.False(s.Equals((object)"Hello"));
    }

    [Fact]
    public void GetHashCode_SameValue_SameHash()
    {
        var a = String255.FromString("Hello");
        var b = String255.FromString("Hello");
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
