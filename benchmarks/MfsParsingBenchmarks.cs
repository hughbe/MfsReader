using BenchmarkDotNet.Attributes;
using MfsReader;

namespace MfsReader.Benchmarks;

/// <summary>
/// Micro-benchmarks for low-level parsing operations.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class MfsParsingBenchmarks
{
    private byte[] _masterDirectoryBlockData = null!;
    private byte[] _fileDirectoryBlockData = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Read a sample MDB from disk
        var diskData = File.ReadAllBytes("Samples/mfs400K.dsk");
        
        // MDB starts at offset 1024
        _masterDirectoryBlockData = new byte[MfsMasterDirectoryBlock.Size];
        Array.Copy(diskData, 1024, _masterDirectoryBlockData, 0, MfsMasterDirectoryBlock.Size);
        
        // Read a file directory block - starts after MDB at FileDirectoryStart
        // For testing, read 64 bytes starting at a typical file directory location
        using var ms = new MemoryStream(diskData, writable: false);
        var disk = new MfsDisk(ms);
        var volume = disk.Volumes[0];
        
        // File directory starts at FileDirectoryStart * 512
        int fileDirectoryOffset = volume.MasterDirectoryBlock.FileDirectoryStart * 512;
        _fileDirectoryBlockData = new byte[MfsFileDirectoryBlock.MinSize + 32]; // Room for name
        Array.Copy(diskData, fileDirectoryOffset, _fileDirectoryBlockData, 0, _fileDirectoryBlockData.Length);
    }

    [Benchmark]
    public MfsMasterDirectoryBlock ParseMasterDirectoryBlock()
    {
        return new MfsMasterDirectoryBlock(_masterDirectoryBlockData);
    }

    [Benchmark]
    public MfsFileDirectoryBlock ParseFileDirectoryBlock()
    {
        return new MfsFileDirectoryBlock(_fileDirectoryBlockData, out _);
    }
}
