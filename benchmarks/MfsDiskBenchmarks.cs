using BenchmarkDotNet.Attributes;
using MfsReader;

namespace MfsReader.Benchmarks;

/// <summary>
/// Benchmarks for MfsDisk and MfsVolume parsing operations.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class MfsDiskBenchmarks
{
    private byte[] _mfs400KData = null!;
    private byte[] _mfs800KData = null!;
    private byte[] _mfs1440KData = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mfs400KData = File.ReadAllBytes("Samples/mfs400K.dsk");
        _mfs800KData = File.ReadAllBytes("Samples/mfs800K.dsk");
        _mfs1440KData = File.ReadAllBytes("Samples/mfs1440K.dsk");
    }

    [Benchmark(Baseline = true)]
    public MfsDisk ParseDisk_400K()
    {
        using var stream = new MemoryStream(_mfs400KData, writable: false);
        return new MfsDisk(stream);
    }

    [Benchmark]
    public MfsDisk ParseDisk_800K()
    {
        using var stream = new MemoryStream(_mfs800KData, writable: false);
        return new MfsDisk(stream);
    }

    [Benchmark]
    public MfsDisk ParseDisk_1440K()
    {
        using var stream = new MemoryStream(_mfs1440KData, writable: false);
        return new MfsDisk(stream);
    }
}
