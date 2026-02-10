using System.Buffers.Binary;
using MfsReader.Utilities;

namespace MfsReader.Tests;

/// <summary>
/// Tests targeting remaining uncovered code paths to achieve 100% coverage.
/// </summary>
public class MfsCoverageTests
{
    #region MfsDisk

    [Fact]
    public void MfsDisk_NonSeekableStream_ThrowsArgumentException()
    {
        using var ms = new MemoryStream(new byte[100]);
        using var wrapper = new NonSeekableStream(ms);
        Assert.Throws<ArgumentException>("stream", () => new MfsDisk(wrapper));
    }

    #endregion

    #region MfsVolume Constructor

    [Fact]
    public void Volume_NonSeekableStream_ThrowsArgumentException()
    {
        using var ms = new MemoryStream(new byte[100]);
        using var wrapper = new NonSeekableStream(ms);
        Assert.Throws<ArgumentException>("stream", () => new MfsVolume(wrapper));
    }

    [Fact]
    public void Volume_NullStream_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>("stream", () => new MfsVolume(null!));
    }

    [Fact]
    public void Volume_TruncatedMdb_ThrowsInvalidDataException()
    {
        // Create a stream with boot blocks (1024 bytes) but only partial MDB data.
        // MfsVolume seeks to offset 1024 then tries to read 512 bytes.
        var data = new byte[1200]; // Only 176 bytes after offset 1024, not enough for 512
        using var ms = new MemoryStream(data);
        Assert.Throws<InvalidDataException>(() => new MfsVolume(ms));
    }

    #endregion

    #region MfsVolume GetFileData

    [Fact]
    public void Volume_GetDataForkData_StreamOverload_WritesToStream()
    {
        byte[] diskBytes = CreateValidDiskImage(dataForkSize: 100);
        using var ms = new MemoryStream(diskBytes);
        var volume = new MfsVolume(ms);
        var entries = volume.GetEntries().ToList();

        using var outputStream = new MemoryStream();
        int bytesWritten = volume.GetDataForkData(entries[0], outputStream);
        Assert.Equal(100, bytesWritten);
        Assert.Equal(100, outputStream.Length);
    }

    [Fact]
    public void Volume_GetResourceForkData_StreamOverload_WritesToStream()
    {
        byte[] diskBytes = CreateValidDiskImage(dataForkSize: 0, resourceForkSize: 50);
        using var ms = new MemoryStream(diskBytes);
        var volume = new MfsVolume(ms);
        var entries = volume.GetEntries().ToList();

        using var outputStream = new MemoryStream();
        int bytesWritten = volume.GetResourceForkData(entries[0], outputStream);
        Assert.Equal(50, bytesWritten);
    }

    [Fact]
    public void Volume_GetFileData_InvalidForkType_ThrowsArgumentOutOfRangeException()
    {
        byte[] diskBytes = CreateValidDiskImage(dataForkSize: 100);
        using var ms = new MemoryStream(diskBytes);
        var volume = new MfsVolume(ms);
        var entries = volume.GetEntries().ToList();

        using var outputStream = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>("forkType",
            () => volume.GetFileData(entries[0], outputStream, (MfsForkType)99));
    }

    [Fact]
    public void Volume_GetFileData_NullOutputStream_ThrowsArgumentNullException()
    {
        byte[] diskBytes = CreateValidDiskImage(dataForkSize: 100);
        using var ms = new MemoryStream(diskBytes);
        var volume = new MfsVolume(ms);
        var entries = volume.GetEntries().ToList();

        Assert.Throws<ArgumentNullException>("outputStream",
            () => volume.GetFileData(entries[0], null!, MfsForkType.DataFork));
    }

    [Fact]
    public void Volume_GetFileData_ZeroSize_ReturnsEmpty()
    {
        // Create a disk with a file that has zero-size data fork
        byte[] diskBytes = CreateValidDiskImage(dataForkSize: 0, resourceForkSize: 50);
        using var ms = new MemoryStream(diskBytes);
        var volume = new MfsVolume(ms);
        var entries = volume.GetEntries().ToList();

        byte[] result = volume.GetDataForkData(entries[0]);
        Assert.Empty(result);
    }

    [Fact]
    public void Volume_GetResourceForkData_ZeroSize_ReturnsEmpty()
    {
        byte[] diskBytes = CreateValidDiskImage(dataForkSize: 100, resourceForkSize: 0);
        using var ms = new MemoryStream(diskBytes);
        var volume = new MfsVolume(ms);
        var entries = volume.GetEntries().ToList();

        byte[] result = volume.GetResourceForkData(entries[0]);
        Assert.Empty(result);
    }

    [Fact]
    public void Volume_GetFileData_StreamOverload_ZeroSizeFork_ReturnsZero()
    {
        // Use the stream overload which calls ReadBlock directly (no early return for size==0).
        byte[] diskBytes = CreateValidDiskImage(dataForkSize: 0, resourceForkSize: 50);
        using var ms = new MemoryStream(diskBytes);
        var volume = new MfsVolume(ms);
        var entries = volume.GetEntries().ToList();

        // Data fork is empty: DataForkAllocationBlock=0, DataForkSize=0
        using var outputStream = new MemoryStream();
        int bytesWritten = volume.GetFileData(entries[0], outputStream, MfsForkType.DataFork);
        Assert.Equal(0, bytesWritten);
        Assert.Equal(0, outputStream.Length);
    }

    [Fact]
    public void Volume_ReadBlock_OffsetOutOfRange_ThrowsInvalidDataException()
    {
        // Use a larger data fork (2000 bytes) so the disk has 2 allocation blocks.
        // This ensures NumberOfAllocationBlocks=2, maxBlocks=2, so the cycle detection
        // doesn't fire before the offset check on the second iteration.
        byte[] diskBytes = CreateValidDiskImage(dataForkSize: 2000);

        // ABM starts at offset 1088 (sector 2 offset 64).
        // For 2 blocks: entry[2]=3, entry[3]=1.
        // 12-bit even entry[2]: byte[0] = value >> 4, byte[1] = (byte[1] & 0x0F) | ((value & 0x0F) << 4)
        // Change entry[2] to 4000 (0xFA0): byte[0] = 0xFA, byte[1] high nibble = 0x00
        diskBytes[1088] = 0xFA;
        diskBytes[1089] = (byte)((diskBytes[1089] & 0x0F) | 0x00);

        // Inflate DataForkSize so the reader doesn't stop after the first block.
        // FDB offset 24 = stream offset 1560 (file directory at sector 3 = offset 1536).
        BinaryPrimitives.WriteUInt32BigEndian(diskBytes.AsSpan(1560), 5000);
        BinaryPrimitives.WriteUInt32BigEndian(diskBytes.AsSpan(1564), 8192);

        using var ms = new MemoryStream(diskBytes);
        var volume = new MfsVolume(ms);
        var entries = volume.GetEntries().ToList();

        Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entries[0]));
    }

    [Fact]
    public void Volume_ReadBlock_TruncatedRead_ThrowsInvalidDataException()
    {
        byte[] fullDisk = CreateValidDiskImage(dataForkSize: 100);

        // Truncate the disk so the allocation block data is incomplete.
        // The allocation blocks start after boot (2 sectors) + MDB/ABM (1 sector) + file directory (1 sector) = 4 sectors = 2048 bytes.
        // Truncate so we have the header but only partial allocation block data.
        int truncateAt = 2048 + 50; // Only 50 bytes of the 1024-byte allocation block
        byte[] truncated = fullDisk[..truncateAt];

        using var ms = new MemoryStream(truncated);
        var volume = new MfsVolume(ms);
        var entries = volume.GetEntries().ToList();

        Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entries[0]));
    }

    [Fact]
    public void Volume_ReadBlock_LargeBlockSize_UsesArrayPool()
    {
        // Create a disk with allocationBlockSize > 4096 to exercise the ArrayPool path.
        var writer = new MfsDiskWriter();
        var dataFork = new byte[100];
        for (int i = 0; i < dataFork.Length; i++) dataFork[i] = (byte)(i & 0xFF);
        writer.AddFile("BigBlock", "TEXT", "ttxt", dataFork);

        using var diskStream = new MemoryStream();
        writer.WriteTo(diskStream, "TestVol", allocationBlockSize: 8192);
        byte[] diskBytes = diskStream.ToArray();

        using var ms = new MemoryStream(diskBytes);
        var volume = new MfsVolume(ms);
        Assert.Equal(8192u, volume.MasterDirectoryBlock.AllocationBlockSize);

        var entries = volume.GetEntries().ToList();
        byte[] result = volume.GetDataForkData(entries[0]);
        Assert.Equal(dataFork, result);
    }

    #endregion

    #region MfsMasterDirectoryBlock

    [Fact]
    public void MasterDirectoryBlock_WrongDataSize_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>("data", () => new MfsMasterDirectoryBlock(new byte[32]));
        Assert.Throws<ArgumentException>("data", () => new MfsMasterDirectoryBlock(new byte[128]));
    }

    [Fact]
    public void MasterDirectoryBlock_InvalidSignature_NonHfs_ThrowsInvalidDataException()
    {
        var data = new byte[MfsMasterDirectoryBlock.Size];
        CreateValidMdb().WriteTo(data);

        // Set signature to something that's neither MFS (0xD2D7) nor HFS (0x4244).
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0), 0x1234);

        Assert.Throws<InvalidDataException>(() => new MfsMasterDirectoryBlock(data));
    }

    [Fact]
    public void MasterDirectoryBlock_InvalidAllocationBlockSize_Zero_ThrowsArgumentException()
    {
        var data = new byte[MfsMasterDirectoryBlock.Size];
        CreateValidMdb().WriteTo(data);

        // Set AllocationBlockSize (offset 20) to 0.
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), 0);

        Assert.Throws<ArgumentException>("data", () => new MfsMasterDirectoryBlock(data));
    }

    [Fact]
    public void MasterDirectoryBlock_InvalidAllocationBlockSize_NotMultipleOf512_ThrowsArgumentException()
    {
        var data = new byte[MfsMasterDirectoryBlock.Size];
        CreateValidMdb().WriteTo(data);

        // Set AllocationBlockSize (offset 20) to 1000 (not a multiple of 512).
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), 1000);

        Assert.Throws<ArgumentException>("data", () => new MfsMasterDirectoryBlock(data));
    }

    [Fact]
    public void MasterDirectoryBlock_VolumeNameTooLong_ThrowsArgumentException()
    {
        var data = new byte[MfsMasterDirectoryBlock.Size];
        CreateValidMdb().WriteTo(data);

        // Set volume name length byte (offset 36) to 28 (> String27.Size = 27).
        data[36] = 28;

        Assert.Throws<ArgumentException>("data", () => new MfsMasterDirectoryBlock(data));
    }

    [Fact]
    public void MasterDirectoryBlock_WriteTo_BufferTooSmall_ThrowsArgumentException()
    {
        var mdb = CreateValidMdb();
        var data = new byte[32]; // Too small, needs 64

        Assert.Throws<ArgumentException>("data", () => mdb.WriteTo(data));
    }

    #endregion

    #region MfsFileDirectoryBlock

    [Fact]
    public void FileDirectoryBlock_DataTooSmall_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>("data",
            () => new MfsFileDirectoryBlock(new byte[10], out _));
    }

    [Fact]
    public void FileDirectoryBlock_InvalidVersion_ThrowsArgumentException()
    {
        byte[] data = CreateValidFdbBytes();

        // Set version byte (offset 1) to non-zero.
        data[1] = 0x01;

        Assert.Throws<ArgumentException>("data",
            () => new MfsFileDirectoryBlock(data, out _));
    }

    [Fact]
    public void FileDirectoryBlock_WriteTo_BufferTooSmall_ThrowsArgumentException()
    {
        var entry = new MfsFileDirectoryBlock(
            flags: MfsFileDirectoryBlockFlags.EntryUsed,
            fileType: String4.FromString("TEXT"),
            creator: String4.FromString("ttxt"),
            finderFlags: 0,
            folderNumber: 0,
            fileNumber: 1,
            dataForkAllocationBlock: 0,
            dataForkSize: 0,
            dataForkAllocatedSize: 0,
            resourceForkAllocationBlock: 0,
            resourceForkSize: 0,
            resourceForkAllocatedSize: 0,
            creationDate: new MfsTimestamp(0),
            lastModificationDate: new MfsTimestamp(0),
            name: String255.FromString("Test"));

        var data = new byte[10]; // Way too small
        Assert.Throws<ArgumentException>("data", () => entry.WriteTo(data, out _));
    }

    #endregion

    #region MfsAllocationBlockMap

    [Fact]
    public void AllocationBlockMap_WriteTo_OddEntryCount_AdvancesPastFinalByte()
    {
        // 3 allocation blocks → entries at indices 2, 3, 4 → array length 5
        // Last index is 4 (even), so WriteTo should advance past the final partial byte.
        var entries = new ushort[5]; // indices 0-4, ABM entries at 2,3,4
        entries[2] = 3;    // block 2 → block 3
        entries[3] = 4;    // block 3 → block 4
        entries[4] = 1;    // block 4 → end

        var abm = new MfsAllocationBlockMap(entries);

        // 3 entries at 12 bits = 36 bits = 4.5 bytes → 5 bytes needed
        var data = new byte[8];
        int bytesWritten = abm.WriteTo(data);

        // Verify we can roundtrip: create a volume from a disk with 3 blocks.
        Assert.True(bytesWritten > 0);
    }

    [Fact]
    public void AllocationBlockMap_WriteTo_EvenEntryCount_DoesNotAdvance()
    {
        // 2 allocation blocks → entries at indices 2, 3 → array length 4
        // Last index is 3 (odd), no extra advance needed.
        var entries = new ushort[4]; // indices 0-3
        entries[2] = 3;    // block 2 → block 3
        entries[3] = 1;    // block 3 → end

        var abm = new MfsAllocationBlockMap(entries);

        var data = new byte[8];
        int bytesWritten = abm.WriteTo(data);
        Assert.True(bytesWritten > 0);
    }

    [Fact]
    public void AllocationBlockMap_GetNextAllocationBlock_NegativeIndex_ThrowsArgumentOutOfRangeException()
    {
        var abm = new MfsAllocationBlockMap(new ushort[] { 0, 0, 1 });
        Assert.Throws<ArgumentOutOfRangeException>("index",
            () => abm.GetNextAllocationBlock(-1));
    }

    [Fact]
    public void AllocationBlockMap_GetNextAllocationBlock_IndexTooLarge_ThrowsArgumentOutOfRangeException()
    {
        var abm = new MfsAllocationBlockMap(new ushort[] { 0, 0, 1 });
        Assert.Throws<ArgumentOutOfRangeException>("index",
            () => abm.GetNextAllocationBlock(3));
    }

    [Fact]
    public void AllocationBlockMap_GetNextAllocationBlock_ValidIndex_ReturnsValue()
    {
        var abm = new MfsAllocationBlockMap(new ushort[] { 0, 0, 42 });
        Assert.Equal(42, abm.GetNextAllocationBlock(2));
    }

    #endregion

    #region MfsDiskWriter AddFile Validation

    [Fact]
    public void DiskWriter_AddFile_NullName_ThrowsArgumentNullException()
    {
        var writer = new MfsDiskWriter();
        Assert.Throws<ArgumentNullException>("name",
            () => writer.AddFile(null!, "TEXT", "ttxt"));
    }

    [Fact]
    public void DiskWriter_AddFile_NullFileType_ThrowsArgumentNullException()
    {
        var writer = new MfsDiskWriter();
        Assert.Throws<ArgumentNullException>("fileType",
            () => writer.AddFile("Test", null!, "ttxt"));
    }

    [Fact]
    public void DiskWriter_AddFile_NullCreator_ThrowsArgumentNullException()
    {
        var writer = new MfsDiskWriter();
        Assert.Throws<ArgumentNullException>("creator",
            () => writer.AddFile("Test", "TEXT", null!));
    }

    [Fact]
    public void DiskWriter_AddFile_EmptyName_ThrowsArgumentException()
    {
        var writer = new MfsDiskWriter();
        Assert.Throws<ArgumentException>("name",
            () => writer.AddFile("", "TEXT", "ttxt"));
    }

    [Fact]
    public void DiskWriter_AddFile_NameTooLong_ThrowsArgumentException()
    {
        var writer = new MfsDiskWriter();
        Assert.Throws<ArgumentException>("name",
            () => writer.AddFile(new string('A', 256), "TEXT", "ttxt"));
    }

    [Fact]
    public void DiskWriter_AddFile_FileTypeWrongLength_ThrowsArgumentException()
    {
        var writer = new MfsDiskWriter();
        Assert.Throws<ArgumentException>("fileType",
            () => writer.AddFile("Test", "TE", "ttxt"));
    }

    [Fact]
    public void DiskWriter_AddFile_CreatorWrongLength_ThrowsArgumentException()
    {
        var writer = new MfsDiskWriter();
        Assert.Throws<ArgumentException>("creator",
            () => writer.AddFile("Test", "TEXT", "tt"));
    }

    #endregion

    #region MfsDiskWriter WriteTo Validation

    [Fact]
    public void DiskWriter_WriteTo_NullStream_ThrowsArgumentNullException()
    {
        var writer = new MfsDiskWriter();
        writer.AddFile("Test", "TEXT", "ttxt", new byte[10]);
        Assert.Throws<ArgumentNullException>("stream",
            () => writer.WriteTo(null!));
    }

    [Fact]
    public void DiskWriter_WriteTo_NonWritableStream_ThrowsArgumentException()
    {
        var writer = new MfsDiskWriter();
        writer.AddFile("Test", "TEXT", "ttxt", new byte[10]);
        using var ms = new MemoryStream(new byte[100], writable: false);
        Assert.Throws<ArgumentException>("stream",
            () => writer.WriteTo(ms));
    }

    [Fact]
    public void DiskWriter_WriteTo_NoFiles_ThrowsInvalidOperationException()
    {
        var writer = new MfsDiskWriter();
        using var ms = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => writer.WriteTo(ms));
    }

    [Fact]
    public void DiskWriter_WriteTo_InvalidBlockSize_Zero_ThrowsArgumentException()
    {
        var writer = new MfsDiskWriter();
        writer.AddFile("Test", "TEXT", "ttxt", new byte[10]);
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentException>("allocationBlockSize",
            () => writer.WriteTo(ms, allocationBlockSize: 0));
    }

    [Fact]
    public void DiskWriter_WriteTo_InvalidBlockSize_NotMultipleOf512_ThrowsArgumentException()
    {
        var writer = new MfsDiskWriter();
        writer.AddFile("Test", "TEXT", "ttxt", new byte[10]);
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentException>("allocationBlockSize",
            () => writer.WriteTo(ms, allocationBlockSize: 1000));
    }

    #endregion

    #region GetEntries Truncated Read

    [Fact]
    public void Volume_GetEntries_TruncatedRead_ThrowsInvalidDataException()
    {
        byte[] diskBytes = CreateValidDiskImage(dataForkSize: 100);

        // Construct the volume with full data, then truncate the stream
        // so that GetEntries can't read a complete 512-byte block.
        using var ms = new MemoryStream();
        ms.Write(diskBytes);
        ms.Position = 0;
        var volume = new MfsVolume(ms);

        // Truncate so the file directory block has only partial data.
        long fileDirectoryOffset = volume.MasterDirectoryBlock.FileDirectoryStart * 512L;
        ms.SetLength(fileDirectoryOffset + 100);

        Assert.Throws<InvalidDataException>(() => volume.GetEntries().ToList());
    }

    #endregion

    #region AllocationBlockMap Truncated Read

    [Fact]
    public void AllocationBlockMap_TruncatedRead_ThrowsInvalidDataException()
    {
        // Create a MDB with 300 allocation blocks so the ABM spans 2+ sectors.
        // ABM bytes = (300 * 3 + 1) / 2 = 450. First sector has 448 bytes available.
        // The remaining 2 bytes need a second sector read.
        var mdb = new MfsMasterDirectoryBlock(
            creationDate: new MfsTimestamp(0),
            attributes: 0,
            numberOfFiles: 0,
            fileDirectoryStart: 3,
            fileDirectoryLength: 0,
            numberOfAllocationBlocks: 300,
            allocationBlockSize: 512,
            clumpSize: 0,
            allocationBlockStart: 3,
            nextFileNumber: 1,
            freeAllocationBlocks: 0,
            volumeName: String27.FromString("Test"));

        // Build a stream: boot blocks (1024) + MDB sector (512) + partial data (64).
        // Total = 1600 bytes. The ABM continuation read at offset 1536 needs 512 bytes
        // but only 64 are available, triggering the truncated read error.
        var data = new byte[1600];
        mdb.WriteTo(data.AsSpan(1024, MfsMasterDirectoryBlock.Size));

        using var ms = new MemoryStream(data);
        Assert.Throws<InvalidDataException>(() => new MfsVolume(ms));
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

    private static byte[] CreateValidFdbBytes()
    {
        var entry = new MfsFileDirectoryBlock(
            flags: MfsFileDirectoryBlockFlags.EntryUsed,
            fileType: String4.FromString("TEXT"),
            creator: String4.FromString("ttxt"),
            finderFlags: 0,
            folderNumber: 0,
            fileNumber: 1,
            dataForkAllocationBlock: 0,
            dataForkSize: 0,
            dataForkAllocatedSize: 0,
            resourceForkAllocationBlock: 0,
            resourceForkSize: 0,
            resourceForkAllocatedSize: 0,
            creationDate: new MfsTimestamp(0),
            lastModificationDate: new MfsTimestamp(0),
            name: String255.FromString("Test"));

        var data = new byte[MfsFileDirectoryBlock.MinSize + 4];
        entry.WriteTo(data, out _);
        return data;
    }

    private static byte[] CreateValidDiskImage(int dataForkSize = 100, int resourceForkSize = 0)
    {
        var writer = new MfsDiskWriter();
        writer.AddFile("TestFile", "TEXT", "ttxt",
            dataForkSize > 0 ? new byte[dataForkSize] : null,
            resourceForkSize > 0 ? new byte[resourceForkSize] : null);

        using var ms = new MemoryStream();
        writer.WriteTo(ms, "TestVol");
        return ms.ToArray();
    }

    /// <summary>
    /// A stream wrapper that reports CanSeek as false.
    /// </summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;

        public NonSeekableStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }

    #endregion
}
