using BenchmarkDotNet.Attributes;
using MfsReader;

namespace MfsReader.Benchmarks;

/// <summary>
/// Benchmarks for MfsVolume operations like reading entries and file data.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class MfsVolumeBenchmarks
{
    private byte[] _diskData = null!;
    private MemoryStream _stream = null!;
    private MfsDisk _disk = null!;
    private MfsVolume _volume = null!;
    private MfsFileDirectoryBlock _largestFile;

    [GlobalSetup]
    public void Setup()
    {
        _diskData = File.ReadAllBytes("Samples/mfs400K.dsk");
        _stream = new MemoryStream(_diskData, writable: false);
        _disk = new MfsDisk(_stream);
        _volume = _disk.Volumes[0];

        // Find the largest file for data reading benchmarks
        _largestFile = _volume.GetEntries()
            .OrderByDescending(f => f.DataForkSize + f.ResourceForkSize)
            .First();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stream.Dispose();
    }

    [Benchmark]
    public int GetEntries()
    {
        int count = 0;
        foreach (var entry in _volume.GetEntries())
        {
            count++;
        }
        return count;
    }

    [Benchmark]
    public List<MfsFileDirectoryBlock> GetEntriesToList()
    {
        return _volume.GetEntries().ToList();
    }

    [Benchmark]
    public byte[] ReadDataFork()
    {
        return _volume.GetDataForkData(_largestFile);
    }

    [Benchmark]
    public byte[] ReadResourceFork()
    {
        return _volume.GetResourceForkData(_largestFile);
    }

    [Benchmark]
    public int ReadDataForkToStream()
    {
        using var ms = new MemoryStream();
        return _volume.GetDataForkData(_largestFile, ms);
    }
}
