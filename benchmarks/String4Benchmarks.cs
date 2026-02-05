using BenchmarkDotNet.Attributes;
using MfsReader.Utilities;

namespace MfsReader.Benchmarks;

/// <summary>
/// Benchmarks for String4 inline array struct.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class String4Benchmarks
{
    private byte[] _data = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = "APPL"u8.ToArray();
    }

    [Benchmark(Baseline = true)]
    public String4 CreateString4()
    {
        return new String4(_data);
    }

    [Benchmark]
    public bool EqualsSpan()
    {
        var s = new String4(_data);
        return s.Equals("APPL"u8);
    }

    [Benchmark]
    public bool EqualsCharSpan()
    {
        var s = new String4(_data);
        return s.Equals("APPL".AsSpan());
    }

    [Benchmark]
    public string ToString_Allocated()
    {
        var s = new String4(_data);
        return s.ToString();
    }
}
