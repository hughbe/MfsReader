using System.Buffers.Binary;
using MfsReader.Utilities;

namespace MfsReader.Tests;

public class MfsValidationTests
{
    #region MfsMasterDirectoryBlock Validation

    [Fact]
    public void MasterDirectoryBlock_ClumpSizeNotMultipleOfAllocationBlockSize_ThrowsInvalidDataException()
    {
        var data = new byte[MfsMasterDirectoryBlock.Size];
        CreateValidMdb().WriteTo(data);

        // Set ClumpSize (offset 24) to 1025 — not a multiple of AllocationBlockSize (1024).
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 1025);

        Assert.Throws<InvalidDataException>(() => new MfsMasterDirectoryBlock(data));
    }

    [Fact]
    public void MasterDirectoryBlock_ClumpSizeZero_DoesNotThrow()
    {
        var data = new byte[MfsMasterDirectoryBlock.Size];
        CreateValidMdb().WriteTo(data);

        // ClumpSize of 0 is allowed (means "use default").
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 0);

        var mdb = new MfsMasterDirectoryBlock(data);
        Assert.Equal(0u, mdb.ClumpSize);
    }

    [Fact]
    public void MasterDirectoryBlock_FreeAllocationBlocksExceedsTotal_ThrowsInvalidDataException()
    {
        var data = new byte[MfsMasterDirectoryBlock.Size];
        CreateValidMdb(numberOfAllocationBlocks: 10, freeAllocationBlocks: 5).WriteTo(data);

        // Set FreeAllocationBlocks (offset 34) to 11, exceeding NumberOfAllocationBlocks (10).
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(34), 11);

        Assert.Throws<InvalidDataException>(() => new MfsMasterDirectoryBlock(data));
    }

    [Fact]
    public void MasterDirectoryBlock_FreeAllocationBlocksEqualToTotal_DoesNotThrow()
    {
        var data = new byte[MfsMasterDirectoryBlock.Size];
        CreateValidMdb(numberOfAllocationBlocks: 10, freeAllocationBlocks: 10).WriteTo(data);

        var mdb = new MfsMasterDirectoryBlock(data);
        Assert.Equal((ushort)10, mdb.FreeAllocationBlocks);
        Assert.Equal((ushort)10, mdb.NumberOfAllocationBlocks);
    }

    [Fact]
    public void MasterDirectoryBlock_NumberOfAllocationBlocksExceeds12BitLimit_ThrowsInvalidDataException()
    {
        var data = new byte[MfsMasterDirectoryBlock.Size];
        CreateValidMdb(numberOfAllocationBlocks: 100, freeAllocationBlocks: 0).WriteTo(data);

        // Set NumberOfAllocationBlocks (offset 18) to 4095, exceeding the 12-bit ABM limit of 4094.
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(18), 4095);

        Assert.Throws<InvalidDataException>(() => new MfsMasterDirectoryBlock(data));
    }

    [Fact]
    public void MasterDirectoryBlock_NumberOfAllocationBlocksAtLimit_DoesNotThrow()
    {
        var data = new byte[MfsMasterDirectoryBlock.Size];
        CreateValidMdb(numberOfAllocationBlocks: 4094, freeAllocationBlocks: 0).WriteTo(data);

        var mdb = new MfsMasterDirectoryBlock(data);
        Assert.Equal((ushort)4094, mdb.NumberOfAllocationBlocks);
    }

    #endregion

    #region MfsFileDirectoryBlock Validation

    [Fact]
    public void FileDirectoryBlock_DataForkSizeExceedsAllocatedSize_ThrowsInvalidDataException()
    {
        byte[] data = CreateValidFdbBytes(
            dataForkAllocationBlock: 2, dataForkSize: 0, dataForkAllocatedSize: 1024);

        // Set DataForkSize (offset 24) to 2048, exceeding DataForkAllocatedSize (1024).
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 2048);

        Assert.Throws<InvalidDataException>(() => new MfsFileDirectoryBlock(data, out _));
    }

    [Fact]
    public void FileDirectoryBlock_ResourceForkSizeExceedsAllocatedSize_ThrowsInvalidDataException()
    {
        byte[] data = CreateValidFdbBytes(
            resourceForkAllocationBlock: 2, resourceForkSize: 0, resourceForkAllocatedSize: 1024);

        // Set ResourceForkSize (offset 34) to 2048, exceeding ResourceForkAllocatedSize (1024).
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(34), 2048);

        Assert.Throws<InvalidDataException>(() => new MfsFileDirectoryBlock(data, out _));
    }

    [Fact]
    public void FileDirectoryBlock_DataForkSizeNonZeroButNoAllocationBlock_ThrowsInvalidDataException()
    {
        byte[] data = CreateValidFdbBytes(
            dataForkAllocationBlock: 0, dataForkSize: 0, dataForkAllocatedSize: 1024);

        // Set DataForkSize (offset 24) to 100. Allocation block (offset 22) is 0.
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 100);

        Assert.Throws<InvalidDataException>(() => new MfsFileDirectoryBlock(data, out _));
    }

    [Fact]
    public void FileDirectoryBlock_ResourceForkSizeNonZeroButNoAllocationBlock_ThrowsInvalidDataException()
    {
        byte[] data = CreateValidFdbBytes(
            resourceForkAllocationBlock: 0, resourceForkSize: 0, resourceForkAllocatedSize: 1024);

        // Set ResourceForkSize (offset 34) to 100. Allocation block (offset 32) is 0.
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(34), 100);

        Assert.Throws<InvalidDataException>(() => new MfsFileDirectoryBlock(data, out _));
    }

    [Fact]
    public void FileDirectoryBlock_EmptyForks_DoesNotThrow()
    {
        byte[] data = CreateValidFdbBytes();

        var entry = new MfsFileDirectoryBlock(data, out _);
        Assert.Equal(0u, entry.DataForkSize);
        Assert.Equal(0u, entry.ResourceForkSize);
    }

    [Fact]
    public void FileDirectoryBlock_ValidForkSizes_DoesNotThrow()
    {
        byte[] data = CreateValidFdbBytes(
            dataForkAllocationBlock: 2, dataForkSize: 500, dataForkAllocatedSize: 1024,
            resourceForkAllocationBlock: 3, resourceForkSize: 200, resourceForkAllocatedSize: 1024);

        var entry = new MfsFileDirectoryBlock(data, out _);
        Assert.Equal(500u, entry.DataForkSize);
        Assert.Equal(1024u, entry.DataForkAllocatedSize);
        Assert.Equal(200u, entry.ResourceForkSize);
        Assert.Equal(1024u, entry.ResourceForkAllocatedSize);
    }

    #endregion

    #region MfsVolume Stream Bounds Validation

    [Fact]
    public void Volume_FileDirectoryBeyondStreamBounds_ThrowsInvalidDataException()
    {
        byte[] diskBytes = CreateValidDiskImage();

        // Set FileDirectoryStart (MDB offset 14, stream offset 1038) to 0xFFFF.
        BinaryPrimitives.WriteUInt16BigEndian(diskBytes.AsSpan(1038), 0xFFFF);

        using var ms = new MemoryStream(diskBytes);
        Assert.Throws<InvalidDataException>(() => new MfsVolume(ms));
    }

    [Fact]
    public void Volume_AllocationBlockStartBeyondStreamBounds_ThrowsInvalidDataException()
    {
        byte[] diskBytes = CreateValidDiskImage();

        // Set AllocationBlockStart (MDB offset 28, stream offset 1052) to 0xFFFF.
        BinaryPrimitives.WriteUInt16BigEndian(diskBytes.AsSpan(1052), 0xFFFF);

        using var ms = new MemoryStream(diskBytes);
        Assert.Throws<InvalidDataException>(() => new MfsVolume(ms));
    }

    #endregion

    #region Allocation Block Chain Cycle Detection

    [Fact]
    public void Volume_AllocationBlockChainCycle_ThrowsInvalidDataException()
    {
        // Create a disk with a file spanning 2 allocation blocks.
        byte[] diskBytes = CreateValidDiskImage(dataForkSize: 1500);

        // ABM starts at stream offset 1088 (MDB offset 64).
        // For 2 blocks: entry[2]=3, entry[3]=1 (end).
        // 12-bit encoding: byte[0]=0x00, byte[1]=0x30, byte[2]=0x01.
        // Change entry[3] from 1 (end) to 2 (cycle back): byte[2] = 0x02.
        diskBytes[1090] = 0x02;

        // Also inflate DataForkSize so the reader doesn't stop before the cycle is detected.
        // DataForkSize is at file directory entry offset 24, file directory starts at sector 3 (stream offset 1536).
        BinaryPrimitives.WriteUInt32BigEndian(diskBytes.AsSpan(1560), 10000);
        // DataForkAllocatedSize must be >= DataForkSize.
        BinaryPrimitives.WriteUInt32BigEndian(diskBytes.AsSpan(1564), 10240);

        using var ms = new MemoryStream(diskBytes);
        var volume = new MfsVolume(ms);
        var entries = volume.GetEntries().ToList();

        Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entries[0]));
    }

    #endregion

    #region Helpers

    private static MfsMasterDirectoryBlock CreateValidMdb(
        ushort numberOfAllocationBlocks = 10,
        uint allocationBlockSize = 1024,
        uint clumpSize = 1024,
        ushort freeAllocationBlocks = 5)
    {
        return new MfsMasterDirectoryBlock(
            creationDate: new MfsTimestamp(0),
            attributes: 0,
            numberOfFiles: 1,
            fileDirectoryStart: 3,
            fileDirectoryLength: 1,
            numberOfAllocationBlocks: numberOfAllocationBlocks,
            allocationBlockSize: allocationBlockSize,
            clumpSize: clumpSize,
            allocationBlockStart: 4,
            nextFileNumber: 2,
            freeAllocationBlocks: freeAllocationBlocks,
            volumeName: String27.FromString("Test"));
    }

    private static byte[] CreateValidFdbBytes(
        ushort dataForkAllocationBlock = 0,
        uint dataForkSize = 0,
        uint dataForkAllocatedSize = 0,
        ushort resourceForkAllocationBlock = 0,
        uint resourceForkSize = 0,
        uint resourceForkAllocatedSize = 0)
    {
        var entry = new MfsFileDirectoryBlock(
            flags: MfsFileDirectoryBlockFlags.EntryUsed,
            fileType: String4.FromString("TEXT"),
            creator: String4.FromString("ttxt"),
            finderFlags: 0,
            folderNumber: 0,
            fileNumber: 1,
            dataForkAllocationBlock: dataForkAllocationBlock,
            dataForkSize: dataForkSize,
            dataForkAllocatedSize: dataForkAllocatedSize,
            resourceForkAllocationBlock: resourceForkAllocationBlock,
            resourceForkSize: resourceForkSize,
            resourceForkAllocatedSize: resourceForkAllocatedSize,
            creationDate: new MfsTimestamp(0),
            lastModificationDate: new MfsTimestamp(0),
            name: String255.FromString("Test"));

        var data = new byte[MfsFileDirectoryBlock.MinSize + 4]; // 4 = "Test".Length
        entry.WriteTo(data, out _);
        return data;
    }

    private static byte[] CreateValidDiskImage(int dataForkSize = 1500)
    {
        var writer = new MfsDiskWriter();
        writer.AddFile("TestFile", "TEXT", "ttxt", new byte[dataForkSize]);

        using var ms = new MemoryStream();
        writer.WriteTo(ms, "TestVol");
        return ms.ToArray();
    }

    #endregion
}
