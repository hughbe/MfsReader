using System.Buffers.Binary;
using MfsReader.Utilities;

namespace MfsReader.Tests;

public class AuditRegressionTests
{
    [Fact]
    public void GetEntries_AllowsForkReadsDuringLazyEnumeration()
    {
        var writer = new MfsDiskWriter();
        for (int i = 0; i < 12; i++)
        {
            writer.AddFile($"File{i:00}", "TEXT", "ttxt", [(byte)i]);
        }

        using var stream = new MemoryStream();
        writer.WriteTo(stream, "Interleaved");
        stream.Position = 0;

        var volume = new MfsVolume(stream);
        var names = new List<string>();
        foreach (MfsFileDirectoryBlock entry in volume.GetEntries())
        {
            names.Add(entry.Name.ToString());
            Assert.Single(volume.GetDataForkData(entry));
        }

        Assert.Equal(12, names.Count);
        Assert.Equal("File11", names[^1]);
    }

    [Fact]
    public void GetDataForkData_WhenChainEndsEarly_ThrowsInvalidDataException()
    {
        byte[] image = CreateImage(new byte[1500]);

        // Change allocation block 2 from "next block 3" to "end of chain".
        image[1088] = 0;
        image[1089] = (byte)((image[1089] & 0x0f) | 0x10);

        using var stream = new MemoryStream(image);
        var volume = new MfsVolume(stream);
        MfsFileDirectoryBlock entry = Assert.Single(volume.GetEntries());

        Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entry));
    }

    [Fact]
    public void WriteTo_WhenMoreThan4093AllocationBlocksAreNeeded_ThrowsInvalidOperationException()
    {
        // Map entries are 12 bits and $000, $001 and $FFF are reserved, so with block numbers
        // starting at 2 the writer can use at most 4093 blocks (numbers 2 to $FFE).
        var writer = new MfsDiskWriter();
        writer.AddFile("TooLarge", "TEXT", "ttxt", new byte[4094 * 512]);

        using var stream = new MemoryStream();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => writer.WriteTo(stream, allocationBlockSize: 512));

        Assert.Contains("4093", exception.Message);
    }

    [Fact]
    public void WriteTo_With4093AllocationBlocks_NeverWritesTheReservedFffValueAndRoundTrips()
    {
        byte[] data = new byte[4093 * 512];
        new Random(1).NextBytes(data);
        var writer = new MfsDiskWriter();
        writer.AddFile("Largest", "TEXT", "ttxt", data);

        using var stream = new MemoryStream();
        writer.WriteTo(stream, allocationBlockSize: 512);
        stream.Position = 0;
        var volume = new MfsVolume(stream);

        Assert.DoesNotContain((ushort)0xFFF, volume.AllocationBlockMap.Entries);
        Assert.Equal(data, volume.GetDataForkData(Assert.Single(volume.GetEntries())));
    }

    [Theory]
    [InlineData("Bad\0Name", "TEXT", "ttxt", "name")]
    [InlineData("Name", "T\0XT", "ttxt", "fileType")]
    [InlineData("Name", "TEXT", "t\0xt", "creator")]
    public void AddFile_WhenMetadataContainsNull_ThrowsArgumentException(
        string name,
        string fileType,
        string creator,
        string parameterName)
    {
        var writer = new MfsDiskWriter();

        Assert.Throws<ArgumentException>(parameterName,
            () => writer.AddFile(name, fileType, creator));
    }

    [Fact]
    public void WriteTo_WhenVolumeNameContainsNull_ThrowsArgumentException()
    {
        var writer = new MfsDiskWriter();
        writer.AddFile("Name", "TEXT", "ttxt");

        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>("volumeName",
            () => writer.WriteTo(stream, "Bad\0Volume"));
    }

    [Fact]
    public void MacRomanStrings_RoundTripHighBitCharacters()
    {
        String4 string4 = String4.FromString("TéXT");
        String27 string27 = String27.FromString("Résumé");
        String255 string255 = String255.FromString("Café");

        Assert.Equal((byte)0x8e, string4.AsReadOnlySpan()[1]);
        Assert.Equal("TéXT", string4.ToString());
        Assert.Equal("Résumé", string27.ToString());
        Assert.Equal("Café", string255.ToString());
        Assert.True(string255.Equals("Café"));
    }

    [Fact]
    public void MacRomanStrings_RejectUnrepresentableCharacters()
    {
        Assert.ThrowsAny<ArgumentException>(() => String255.FromString("Snowman ☃"));
    }

    [Fact]
    public void MacRomanStringComparisons_ReturnFalseForUnrepresentableCharacters()
    {
        Assert.False(String4.FromString("TEXT").Equals("☃EXT"));
        Assert.False(String27.FromString("Ten chars!").Equals("123456789☃"));
        Assert.False(String255.FromString("Ten chars!").Equals("123456789☃"));
    }

    [Fact]
    public void MacRomanEncoding_GetBytesRejectsEmbeddedNull()
    {
        Assert.Throws<ArgumentException>(() => MacRomanEncoding.GetBytes("Bad\0text", new byte[8]));
    }

    [Fact]
    public void FromDateTime_WhenBeforeMacEpoch_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>("dateTime",
            () => MfsTimestamp.FromDateTime(new DateTime(1903, 12, 31, 23, 59, 59, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void FromDateTime_WhenAfterMaximumTimestamp_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>("dateTime",
            () => MfsTimestamp.FromDateTime(new DateTime(2041, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void VolumeLayout_ComputesCheckedRangesAndBlockOffsets()
    {
        MfsMasterDirectoryBlock mdb = CreateMdb();

        VolumeLayout layout = VolumeLayout.Create(mdb, 100, 4096, 4196);

        Assert.Equal(1188, layout.AllocationMapOffset);
        Assert.Equal(3, layout.AllocationMapLength);
        Assert.Equal(1636, layout.DirectoryOffset);
        Assert.Equal(512, layout.DirectoryLength);
        Assert.Equal(2148, layout.AllocationOffset);
        Assert.Equal(2048, layout.AllocationLength);
        Assert.Equal(100, layout.VolumeStart);
        Assert.Equal(4196, layout.VolumeEnd);
        Assert.Equal(1024, layout.AllocationBlockSize);
        Assert.Equal((ushort)2, layout.AllocationBlockCount);
        Assert.Equal(2148, layout.GetAllocationBlockOffset(2));
        Assert.Equal(3172, layout.GetAllocationBlockOffset(3));
    }

    [Fact]
    public void VolumeLayout_WhenAllocationRegionExceedsVolume_ThrowsInvalidDataException()
    {
        MfsMasterDirectoryBlock mdb = CreateMdb();

        Assert.Throws<InvalidDataException>(() => VolumeLayout.Create(mdb, 0, 4095, 4095));
    }

    [Fact]
    public void VolumeLayout_WhenAllocationMapOverlapsDirectory_ThrowsInvalidDataException()
    {
        MfsMasterDirectoryBlock mdb = CreateMdb(fileDirectoryStart: 2);

        Assert.Throws<InvalidDataException>(() => VolumeLayout.Create(mdb, 0, 4096, 4096));
    }

    [Fact]
    public void VolumeLayout_WhenDirectoryOverlapsAllocationRegion_ThrowsInvalidDataException()
    {
        MfsMasterDirectoryBlock mdb = CreateMdb(fileDirectoryLength: 2);

        Assert.Throws<InvalidDataException>(() => VolumeLayout.Create(mdb, 0, 4096, 4096));
    }

    [Fact]
    public void VolumeLayout_WhenDirectoryIsWhollyInsideAllocationRegion_IsAccepted()
    {
        // Allocation region: sectors 4-7. Directory: sector 5.
        MfsMasterDirectoryBlock mdb = CreateMdb(fileDirectoryStart: 5, numberOfAllocationBlocks: 2);

        VolumeLayout layout = VolumeLayout.Create(mdb, 0, 4096, 4096);

        Assert.Equal(5 * 512, layout.DirectoryOffset);
    }

    [Fact]
    public void VolumeLayout_WhenDirectoryStartsInsideButEndsPastAllocationRegion_ThrowsInvalidDataException()
    {
        // Allocation region: sectors 4-7. Directory: sectors 7-8.
        MfsMasterDirectoryBlock mdb = CreateMdb(
            fileDirectoryStart: 7,
            fileDirectoryLength: 2,
            numberOfAllocationBlocks: 2);

        Assert.Throws<InvalidDataException>(() => VolumeLayout.Create(mdb, 0, 8192, 8192));
    }

    [Fact]
    public void VolumeLayout_WhenAllocationMapOverlapsAllocationRegion_ThrowsInvalidDataException()
    {
        // 4094 map bytes of 12-bit entries starting at byte 1088 run well past sector 4.
        MfsMasterDirectoryBlock mdb = CreateMdb(
            fileDirectoryStart: 20,
            numberOfAllocationBlocks: 4000,
            allocationBlockSize: 512);

        var exception = Assert.Throws<InvalidDataException>(
            () => VolumeLayout.Create(mdb, 0, 1 << 22, 1 << 22));

        Assert.Contains("map overlaps the allocation block region", exception.Message);
    }

    [Theory]
    [InlineData(-1, 4096, 4096)]
    [InlineData(0, -1, 4096)]
    [InlineData(0, 4096, -1)]
    public void VolumeLayout_WhenBoundsAreNegative_ThrowsInvalidDataException(
        long volumeStart,
        long volumeLength,
        long streamLength)
    {
        Assert.Throws<InvalidDataException>(
            () => VolumeLayout.Create(CreateMdb(), volumeStart, volumeLength, streamLength));
    }

    [Fact]
    public void VolumeLayout_WhenVolumeExceedsStream_ThrowsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() => VolumeLayout.Create(CreateMdb(), 1, 4096, 4096));
    }

    [Fact]
    public void VolumeLayout_WhenBlockSizeExceedsImplementationLimit_ThrowsInvalidDataException()
    {
        MfsMasterDirectoryBlock mdb = CreateMdb(allocationBlockSize: uint.MaxValue - 511);

        Assert.Throws<InvalidDataException>(() => VolumeLayout.Create(mdb, 0, long.MaxValue, long.MaxValue));
    }

    [Fact]
    public void VolumeLayout_WhenArithmeticOverflows_ThrowsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(
            () => VolumeLayout.Create(CreateMdb(), long.MaxValue - 10, 20, long.MaxValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void VolumeLayout_WhenBlockNumberIsOutsideVolume_ThrowsInvalidDataException(ushort block)
    {
        VolumeLayout layout = VolumeLayout.Create(CreateMdb(), 0, 4096, 4096);

        Assert.Throws<InvalidDataException>(() => layout.GetAllocationBlockOffset(block));
    }

    [Fact]
    public void FileDirectoryBlock_WhenNameIsEmpty_ThrowsInvalidDataException()
    {
        byte[] data = CreateMinimalDirectoryEntry(nameLength: 0);

        Assert.Throws<InvalidDataException>(() => new MfsFileDirectoryBlock(data, out _));
    }

    [Fact]
    public void FileDirectoryBlock_WhenNameCrossesSectorBoundary_ThrowsInvalidDataException()
    {
        byte[] data = CreateMinimalDirectoryEntry(nameLength: 1);

        Assert.Throws<InvalidDataException>(() => new MfsFileDirectoryBlock(data, out _));
    }

    [Fact]
    public void MasterDirectoryBlock_WhenVolumeNameIsEmpty_ThrowsInvalidDataException()
    {
        byte[] image = CreateImage([1]);
        image[MfsVolume.MasterDirectoryBlockOffset + 36] = 0;

        using var stream = new MemoryStream(image);
        Assert.Throws<InvalidDataException>(() => new MfsVolume(stream));
    }

    [Fact]
    public void MasterDirectoryBlock_ValueConstructorRejectsInvalidFields()
    {
        Assert.Throws<InvalidDataException>(() => CreateMdb(numberOfAllocationBlocks: 4095));
        Assert.Throws<InvalidDataException>(() => CreateMdb(allocationBlockSize: 0));
        Assert.Throws<InvalidDataException>(() => CreateMdb(clumpSize: 513));
        Assert.Throws<InvalidDataException>(() => CreateMdb(freeAllocationBlocks: 3));
        Assert.Throws<InvalidDataException>(() => CreateMdb(emptyVolumeName: true));
    }

    [Fact]
    public void FileDirectoryBlock_ValueConstructorRejectsInvalidFields()
    {
        Assert.Throws<InvalidDataException>(() => CreateDirectoryEntry(
            dataForkAllocationBlock: 2,
            dataForkSize: 2,
            dataForkAllocatedSize: 1));
        Assert.Throws<InvalidDataException>(() => CreateDirectoryEntry(
            dataForkSize: 1,
            dataForkAllocatedSize: 1));
        Assert.Throws<InvalidDataException>(() => CreateDirectoryEntry(emptyName: true));
    }

    [Fact]
    public void AllocationBlockMap_RejectsInvalidConstructionArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new MfsAllocationBlockMap(null!));
        Assert.Throws<ArgumentException>(() => new MfsAllocationBlockMap(new ushort[1]));
        Assert.Throws<ArgumentException>(() => new MfsAllocationBlockMap(new ushort[4097]));
        // Values that cannot be encoded in a 12-bit entry are rejected...
        Assert.Throws<InvalidDataException>(() => new MfsAllocationBlockMap([0, 0, 0x1000]));

        // ...but pointers to nonexistent blocks are only rejected when followed.
        Assert.Equal(3, new MfsAllocationBlockMap([0, 0, 3]).GetNextAllocationBlock(2));
    }

    [Fact]
    public void AllocationBlockMap_WriteToRejectsSmallDestination()
    {
        var map = new MfsAllocationBlockMap([0, 0, 1]);

        Assert.Throws<ArgumentException>(() => map.WriteTo(new byte[1]));
    }

    [Fact]
    public void AllocationBlockMap_WriteToSupportsEmptyMap()
    {
        var map = new MfsAllocationBlockMap([0, 0]);

        Assert.Equal(0, map.WriteTo([]));
    }

    [Fact]
    public void AllocationBlockMap_SnapshotsConstructorInput()
    {
        ushort[] entries = [0, 0, 1];
        var map = new MfsAllocationBlockMap(entries);

        entries[2] = 0;

        Assert.Equal((ushort)1, map.GetNextAllocationBlock(2));
    }

    [Fact]
    public void Timestamp_RejectsShortBuffers()
    {
        Assert.Throws<ArgumentException>(() => new MfsTimestamp(new byte[3]));

        var timestamp = new MfsTimestamp(0);
        Assert.Throws<ArgumentException>(() => timestamp.WriteTo(new byte[3]));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2147483648u)]
    public void DiskWriter_RejectsUnsupportedAllocationBlockSizes(uint allocationBlockSize)
    {
        var writer = new MfsDiskWriter();
        writer.AddFile("Name", "TEXT", "ttxt");

        using var stream = new MemoryStream();
        Assert.ThrowsAny<ArgumentException>(() => writer.WriteTo(stream, allocationBlockSize: allocationBlockSize));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567890123456789012345678")]
    [InlineData("Snowman ☃")]
    public void DiskWriter_RejectsInvalidVolumeNames(string volumeName)
    {
        var writer = new MfsDiskWriter();
        writer.AddFile("Name", "TEXT", "ttxt");

        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>("volumeName", () => writer.WriteTo(stream, volumeName));
    }

    [Fact]
    public void DiskWriter_SnapshotsForkDataAndSetsEntryUsedFlag()
    {
        byte[] data = [1, 2, 3];
        var writer = new MfsDiskWriter();
        writer.AddFile("Name", "TEXT", "ttxt", data, flags: MfsFileDirectoryBlockFlags.Locked);
        data[0] = 99;

        using var stream = new MemoryStream();
        writer.WriteTo(stream);
        stream.Position = 0;

        var volume = new MfsVolume(stream);
        MfsFileDirectoryBlock entry = Assert.Single(volume.GetEntries());
        Assert.Equal([1, 2, 3], volume.GetDataForkData(entry));
        Assert.True(entry.Flags.HasFlag(MfsFileDirectoryBlockFlags.EntryUsed));
        Assert.True(entry.Flags.HasFlag(MfsFileDirectoryBlockFlags.Locked));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100000)]
    public void BoundedVolume_WhenLengthIsOutsideStream_ThrowsInvalidDataException(long volumeLength)
    {
        byte[] image = CreateImage([1]);
        using var stream = new MemoryStream(image);

        Assert.Throws<InvalidDataException>(() => new MfsVolume(stream, volumeLength));
    }

    [Fact]
    public void BoundedVolume_WhenPartitionIsTooSmallForMdb_ThrowsInvalidDataException()
    {
        byte[] image = CreateImage([1]);
        using var stream = new MemoryStream(image);

        Assert.Throws<InvalidDataException>(() => new MfsVolume(stream, 1024));
    }

    [Fact]
    public void AllocationBlockMap_RejectsNullVolumeAndShortInitialData()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MfsAllocationBlockMap(new byte[MfsMasterDirectoryBlock.Size], null!));

        byte[] image = CreateImage([1]);
        using var stream = new MemoryStream(image);
        var volume = new MfsVolume(stream);
        Assert.Throws<ArgumentException>(
            () => new MfsAllocationBlockMap(new byte[MfsMasterDirectoryBlock.Size - 1], volume));
    }

    [Fact]
    public void GetFileData_RejectsReadOnlyOutputStream()
    {
        byte[] image = CreateImage([1]);
        using var stream = new MemoryStream(image);
        var volume = new MfsVolume(stream);
        MfsFileDirectoryBlock entry = Assert.Single(volume.GetEntries());
        using var output = new MemoryStream(new byte[1], writable: false);

        Assert.Throws<ArgumentException>("outputStream",
            () => volume.GetDataForkData(entry, output));
    }

    [Fact]
    public void GetFileData_RejectsForkSizeLargerThanReturnType()
    {
        byte[] image = CreateImage([1]);
        using var stream = new MemoryStream(image);
        var volume = new MfsVolume(stream);
        var entry = new MfsFileDirectoryBlock(
            flags: MfsFileDirectoryBlockFlags.EntryUsed,
            fileType: String4.FromString("TEXT"),
            creator: String4.FromString("ttxt"),
            finderFlags: 0,
            folderNumber: 0,
            fileNumber: 1,
            dataForkAllocationBlock: 2,
            dataForkSize: uint.MaxValue,
            dataForkAllocatedSize: uint.MaxValue,
            resourceForkAllocationBlock: 0,
            resourceForkSize: 0,
            resourceForkAllocatedSize: 0,
            creationDate: new MfsTimestamp(0),
            lastModificationDate: new MfsTimestamp(0),
            name: String255.FromString("Large"));

        Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entry));

        using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entry, output));
    }

    [Fact]
    public void GetFileData_ArrayOverloadRejectsInvalidForkTypeEvenWhenForkIsEmpty()
    {
        byte[] image = CreateImage([1]);
        using var stream = new MemoryStream(image);
        var volume = new MfsVolume(stream);
        MfsFileDirectoryBlock entry = Assert.Single(volume.GetEntries());

        Assert.Throws<ArgumentOutOfRangeException>("forkType",
            () => volume.GetFileData(entry, (MfsForkType)99));
    }

    [Fact]
    public void GetEntries_WhenDeclaredFileCountDoesNotMatchDirectory_ReturnsDirectoryEntries()
    {
        byte[] image = CreateImage([1]);
        BinaryPrimitives.WriteUInt16BigEndian(
            image.AsSpan(MfsVolume.MasterDirectoryBlockOffset + 12),
            2);

        using var stream = new MemoryStream(image);
        var volume = new MfsVolume(stream);

        Assert.Equal(2, volume.MasterDirectoryBlock.NumberOfFiles);
        Assert.Single(volume.GetEntries());
    }

    [Fact]
    public void DiskWriter_LimitValidationIsDirectlyTestable()
    {
        MfsDiskWriter.ValidateFileCount(ushort.MaxValue);
        Assert.Throws<InvalidOperationException>(() => MfsDiskWriter.ValidateFileCount(-1));
        Assert.Throws<InvalidOperationException>(() => MfsDiskWriter.ValidateFileCount(ushort.MaxValue + 1));

        Assert.Equal(((ushort)2, (ushort)5), MfsDiskWriter.ValidateDirectoryLayout(3, 2));
        Assert.Throws<InvalidOperationException>(() => MfsDiskWriter.ValidateDirectoryLayout(3, 0));
        Assert.Throws<InvalidOperationException>(
            () => MfsDiskWriter.ValidateDirectoryLayout(3, ushort.MaxValue - 2));
    }

    [Fact]
    public void BoundedVolume_WhenBoundsArithmeticOverflows_ThrowsInvalidDataException()
    {
        using var stream = new BoundsOnlyStream(long.MaxValue - 10, long.MaxValue);

        Assert.Throws<InvalidDataException>(() => new MfsVolume(stream, 20));
    }

    [Fact]
    public void BoundedVolume_RejectsUnreadableStream()
    {
        using var stream = new BoundsOnlyStream(0, 4096, canRead: false);

        Assert.Throws<ArgumentException>("stream", () => new MfsVolume(stream, 4096));
    }

    [Fact]
    public void BoundedVolume_RejectsNonSeekableStream()
    {
        using var stream = new BoundsOnlyStream(0, 4096, canSeek: false);

        Assert.Throws<ArgumentException>("stream", () => new MfsVolume(stream, 4096));
    }

    private static byte[] CreateImage(byte[] data)
    {
        var writer = new MfsDiskWriter();
        writer.AddFile("Test", "TEXT", "ttxt", data);
        using var stream = new MemoryStream();
        writer.WriteTo(stream, "Test");
        return stream.ToArray();
    }

    private static byte[] CreateMinimalDirectoryEntry(byte nameLength)
    {
        var data = new byte[MfsFileDirectoryBlock.MinSize];
        data[0] = (byte)MfsFileDirectoryBlockFlags.EntryUsed;
        data[^1] = nameLength;
        return data;
    }

    private static MfsFileDirectoryBlock CreateDirectoryEntry(
        ushort dataForkAllocationBlock = 0,
        uint dataForkSize = 0,
        uint dataForkAllocatedSize = 0,
        bool emptyName = false) =>
        new(
            flags: MfsFileDirectoryBlockFlags.EntryUsed,
            fileType: String4.FromString("TEXT"),
            creator: String4.FromString("ttxt"),
            finderFlags: 0,
            folderNumber: 0,
            fileNumber: 1,
            dataForkAllocationBlock: dataForkAllocationBlock,
            dataForkSize: dataForkSize,
            dataForkAllocatedSize: dataForkAllocatedSize,
            resourceForkAllocationBlock: 0,
            resourceForkSize: 0,
            resourceForkAllocatedSize: 0,
            creationDate: new MfsTimestamp(0),
            lastModificationDate: new MfsTimestamp(0),
            name: emptyName ? default : String255.FromString("Test"));

    private static MfsMasterDirectoryBlock CreateMdb(
        ushort fileDirectoryStart = 3,
        ushort fileDirectoryLength = 1,
        uint allocationBlockSize = 1024,
        uint? clumpSize = null,
        ushort numberOfAllocationBlocks = 2,
        ushort freeAllocationBlocks = 0,
        bool emptyVolumeName = false) =>
        new(
            creationDate: new MfsTimestamp(0),
            attributes: 0,
            numberOfFiles: 1,
            fileDirectoryStart: fileDirectoryStart,
            fileDirectoryLength: fileDirectoryLength,
            numberOfAllocationBlocks: numberOfAllocationBlocks,
            allocationBlockSize: allocationBlockSize,
            clumpSize: clumpSize ?? allocationBlockSize,
            allocationBlockStart: 4,
            nextFileNumber: 2,
            freeAllocationBlocks: freeAllocationBlocks,
            volumeName: emptyVolumeName ? default : String27.FromString("Test"));

    private sealed class BoundsOnlyStream(
        long position,
        long length,
        bool canRead = true,
        bool canSeek = true) : Stream
    {
        public override bool CanRead => canRead;
        public override bool CanSeek => canSeek;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; } = position;
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
