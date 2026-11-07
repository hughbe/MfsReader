using System.Buffers.Binary;
using MfsReader.Utilities;

namespace MfsReader.Tests;

/// <summary>
/// Regression tests for how the reader and writer behave on unusual or damaged volumes.
/// </summary>
public class ReaderRobustnessTests
{
    private const int MdbOffset = MfsVolume.MasterDirectoryBlockOffset;

    // ---- Embedded null bytes in names -------------------------------------------------

    [Fact]
    public void FileDirectoryBlock_NameWithEmbeddedNull_ConsumesDeclaredLength()
    {
        // 51 fixed bytes + a 5 byte name "f\0rst". Name is truncated at the null, but all
        // 5 name bytes (and the whole entry) are still consumed. This used to trip a
        // Debug.Assert, which terminates the process in Debug builds.
        byte[] data = new byte[MfsFileDirectoryBlock.MinSize + 5];
        data[0] = (byte)MfsFileDirectoryBlockFlags.EntryUsed;
        data[MfsFileDirectoryBlock.MinSize - 1] = 5;
        "f\0rst"u8.CopyTo(data.AsSpan(MfsFileDirectoryBlock.MinSize));

        var entry = new MfsFileDirectoryBlock(data, out int bytesRead);

        Assert.Equal(data.Length, bytesRead);
        Assert.Equal("f", entry.Name.ToString());
    }

    [Fact]
    public void GetEntries_NameWithEmbeddedNull_StillReturnsFollowingEntries()
    {
        byte[] image = CreateImage(w =>
        {
            w.AddFile("first", "TEXT", "ttxt", [1]);
            w.AddFile("second", "TEXT", "ttxt", [2]);
        });
        image[DirectoryOffset(image) + 51 + 1] = 0; // "f\0rst"

        var volume = new MfsVolume(new MemoryStream(image));
        List<MfsFileDirectoryBlock> entries = volume.GetEntries().ToList();

        Assert.Equal(2, entries.Count);
        Assert.Equal("second", entries[1].Name.ToString());
    }

    // ---- Allocation block map entries -------------------------------------------------

    [Fact]
    public void Volume_StrayReservedMapEntry_DoesNotPreventOpeningOrReadingOtherFiles()
    {
        byte[] image = CreateImage(w =>
        {
            w.AddFile("big", "TEXT", "ttxt", new byte[1500]); // blocks 2 -> 3
            w.AddFile("small", "TEXT", "ttxt", [7]);          // block 4
        });

        // Set the map entry for block 2 ("next block") to 0xFFF, which is not a valid block here.
        int abm = MdbOffset + MfsMasterDirectoryBlock.Size;
        image[abm] = 0xFF;
        image[abm + 1] = (byte)((image[abm + 1] & 0x0F) | 0xF0);

        var volume = new MfsVolume(new MemoryStream(image));
        Assert.Equal(0xFFF, volume.AllocationBlockMap.GetNextAllocationBlock(2));

        List<MfsFileDirectoryBlock> entries = volume.GetEntries().ToList();
        Assert.Equal([7], volume.GetDataForkData(entries[1]));
        Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entries[0]));
    }

    [Fact]
    public void AllocationBlockMap_WriteTo_RejectsEntriesThatDoNotFitIn12Bits()
    {
        var map = new MfsAllocationBlockMap([0, 0, 1]);
        map.Entries[2] = 0x1000;

        Assert.Throws<InvalidDataException>(() => map.WriteTo(new byte[2]));
    }

    // ---- File directory tolerance -----------------------------------------------------

    [Fact]
    public void GetEntries_NonZeroEntryVersion_StillEnumerates()
    {
        byte[] image = CreateImage(w => w.AddFile("first", "TEXT", "ttxt", [1]));
        image[DirectoryOffset(image) + 1] = 2;

        var volume = new MfsVolume(new MemoryStream(image));

        Assert.Equal(2, Assert.Single(volume.GetEntries()).Version);
    }

    [Fact]
    public void GetEntries_ZeroFlagsByteEndsTheBlock_LikeTheMacFileManager()
    {
        // The ROM tests the whole flags byte ("flags=0 means end of entries this blk") and its
        // delete routine keeps entries packed, so nothing valid follows a zero flags byte.
        byte[] image = CreateImage(w =>
        {
            w.AddFile("first", "TEXT", "ttxt", [1]);
            w.AddFile("second", "TEXT", "ttxt", [2]);
        });
        image[DirectoryOffset(image)] = 0;

        var volume = new MfsVolume(new MemoryStream(image));

        Assert.Empty(volume.GetEntries());
        Assert.Equal(2, volume.MasterDirectoryBlock.NumberOfFiles); // declared count is only a hint
    }

    [Fact]
    public void GetEntries_NonZeroFlagsWithoutUsedBit_IsSkippedButLaterEntriesAreKept()
    {
        byte[] image = CreateImage(w =>
        {
            w.AddFile("first", "TEXT", "ttxt", [1]);
            w.AddFile("second", "TEXT", "ttxt", [2]);
        });
        image[DirectoryOffset(image)] = (byte)MfsFileDirectoryBlockFlags.Locked; // bit 7 clear

        var volume = new MfsVolume(new MemoryStream(image));
        MfsFileDirectoryBlock entry = Assert.Single(volume.GetEntries());

        Assert.Equal("second", entry.Name.ToString());
    }

    [Theory]
    [InlineData(0x00)] // zero flags: end of block, regardless of the stale name length
    [InlineData(0x01)] // non-zero without the used bit: a skipped slot that would cross the sector
    public void GetEntries_TailEntryWhoseNameWouldCrossSectorBoundary_EndsTheBlock(byte tailFlags)
    {
        // Four 92-byte entries occupy bytes 0-367 of the sector.
        byte[] image = CreateImage(w =>
        {
            for (int i = 0; i < 4; i++)
            {
                w.AddFile(new string((char)('a' + i), 40), "TEXT", "ttxt", [(byte)i]);
            }
        });

        // A tail slot that claims a 200-byte name: 368 + 51 + 200 > 512.
        int tail = DirectoryOffset(image) + 368;
        image[tail] = tailFlags;
        image[tail + MfsFileDirectoryBlock.MinSize - 1] = 200;

        var volume = new MfsVolume(new MemoryStream(image));

        Assert.Equal(4, volume.GetEntries().Count());
    }

    [Fact]
    public void Volume_DirectoryInsideAllocationArea_IsReadable()
    {
        // Inside Macintosh: the directory "can sit inside the allocation area if its blocks
        // are marked with $FFF's in the block map". 512-byte allocation blocks 2-5 live in
        // sectors 3-6; block 3 (sector 4) holds the directory.
        var mdb = new MfsMasterDirectoryBlock(
            creationDate: new MfsTimestamp(0),
            attributes: 0,
            numberOfFiles: 1,
            fileDirectoryStart: 4,
            fileDirectoryLength: 1,
            numberOfAllocationBlocks: 4,
            allocationBlockSize: 512,
            clumpSize: 512,
            allocationBlockStart: 3,
            nextFileNumber: 2,
            freeAllocationBlocks: 2,
            volumeName: String27.FromString("DirInAlloc"));
        var map = new MfsAllocationBlockMap([0, 0, 1, 0xFFF, 0, 0]);
        var entry = new MfsFileDirectoryBlock(
            flags: MfsFileDirectoryBlockFlags.EntryUsed,
            fileType: String4.FromString("TEXT"),
            creator: String4.FromString("ttxt"),
            finderFlags: 0,
            folderNumber: 0,
            fileNumber: 1,
            dataForkAllocationBlock: 2,
            dataForkSize: 3,
            dataForkAllocatedSize: 512,
            resourceForkAllocationBlock: 0,
            resourceForkSize: 0,
            resourceForkAllocatedSize: 0,
            creationDate: new MfsTimestamp(0),
            lastModificationDate: new MfsTimestamp(0),
            name: String255.FromString("note"));

        byte[] image = new byte[7 * VolumeLayout.SectorSize];
        mdb.WriteTo(image.AsSpan(MdbOffset, MfsMasterDirectoryBlock.Size));
        map.WriteTo(image.AsSpan(MdbOffset + MfsMasterDirectoryBlock.Size));
        entry.WriteTo(image.AsSpan(4 * VolumeLayout.SectorSize), out _);
        new byte[] { 9, 8, 7 }.CopyTo(image, 3 * VolumeLayout.SectorSize);

        var volume = new MfsVolume(new MemoryStream(image));

        Assert.Equal(0xFFF, volume.AllocationBlockMap.GetNextAllocationBlock(3));
        MfsFileDirectoryBlock read = Assert.Single(volume.GetEntries());
        Assert.Equal([9, 8, 7], volume.GetDataForkData(read));
    }

    [Theory]
    [InlineData("mfs400K.dsk")]
    [InlineData("mfs800K.dsk")]
    [InlineData("mfs1440K.dsk")]
    [InlineData("SystemAdditions.dsk")]
    [InlineData("SystemStartup.dsk")]
    [InlineData("MacSpeak.dsk")]
    public void SampleDisks_EnumerateExactlyTheDeclaredNumberOfFiles(string diskName)
    {
        using var stream = File.OpenRead(Path.Combine("Samples", diskName));
        var volume = new MfsVolume(stream);

        Assert.Equal(volume.MasterDirectoryBlock.NumberOfFiles, volume.GetEntries().Count());
    }

    // ---- Stream position and truncation -----------------------------------------------

    [Fact]
    public void Volume_StartsAtTheStreamsCurrentPosition()
    {
        byte[] image = CreateImage(w => w.AddFile("first", "TEXT", "ttxt", [1, 2, 3]));
        byte[] prefixed = new byte[700 + image.Length]; // deliberately not sector aligned
        image.CopyTo(prefixed, 700);

        using var stream = new MemoryStream(prefixed) { Position = 700 };
        var volume = new MfsVolume(stream);

        MfsFileDirectoryBlock entry = Assert.Single(volume.GetEntries());
        Assert.Equal([1, 2, 3], volume.GetDataForkData(entry));
    }

    [Fact]
    public void Volume_WhenPositionIsNotAtTheVolumeStart_ThrowsInvalidDataException()
    {
        byte[] image = CreateImage(w => w.AddFile("first", "TEXT", "ttxt", [1]));
        using var stream = new MemoryStream(image) { Position = 1 };

        Assert.Throws<InvalidDataException>(() => new MfsVolume(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(1024)]
    [InlineData(1100)]
    [InlineData(1535)]
    public void Volume_StreamShorterThanMdbSector_ThrowsInvalidDataException(int length)
    {
        using var stream = new MemoryStream(new byte[length]);

        Assert.Throws<InvalidDataException>(() => new MfsVolume(stream));
    }

    // ---- Writer -----------------------------------------------------------------------

    [Theory]
    [InlineData("Read Me", "read me")]
    [InlineData("Read Me", "READ ME")]
    [InlineData("Read Me", "Read Me")]
    [InlineData("Café", "CAFÉ")]
    public void DiskWriter_AddFile_DuplicateNameIgnoringCase_ThrowsArgumentException(string first, string second)
    {
        var writer = new MfsDiskWriter();
        writer.AddFile(first, "TEXT", "ttxt", [1]);

        Assert.Throws<ArgumentException>("name", () => writer.AddFile(second, "TEXT", "ttxt", [2]));
    }

    [Fact]
    public void DiskWriter_AddFile_FailedCallDoesNotReserveTheName()
    {
        var writer = new MfsDiskWriter();
        Assert.Throws<ArgumentException>("fileType", () => writer.AddFile("Name", "TOOLONG", "ttxt"));

        writer.AddFile("Name", "TEXT", "ttxt", [1]);

        using var stream = new MemoryStream();
        writer.WriteTo(stream);
        stream.Position = 0;
        Assert.Single(new MfsVolume(stream).GetEntries());
    }

    [Fact]
    public void DiskWriter_AddFile_DistinctNames_AreAllWritten()
    {
        byte[] image = CreateImage(w =>
        {
            w.AddFile("a", "TEXT", "ttxt", [1]);
            w.AddFile("b", "TEXT", "ttxt", [2]);
        });

        Assert.Equal(2, new MfsVolume(new MemoryStream(image)).GetEntries().Count());
    }

    [Fact]
    public void DiskWriter_EmptyVolume_CanBeOpenedAsDisk()
    {
        using var stream = new MemoryStream();
        new MfsDiskWriter().WriteTo(stream, "Blank", allocationBlockSize: 512);
        stream.Position = 0;

        var disk = new MfsDisk(stream);

        MfsVolume volume = Assert.Single(disk.Volumes);
        Assert.Equal(0, volume.MasterDirectoryBlock.NumberOfAllocationBlocks);
        Assert.Empty(volume.GetEntries());
    }

    // ---- Timestamps -------------------------------------------------------------------

    [Fact]
    public void Writer_RecordsLocalWallClockTime()
    {
        DateTime before = DateTime.Now;
        byte[] image = CreateImage(w => w.AddFile("first", "TEXT", "ttxt", [1]));
        DateTime after = DateTime.Now;

        MfsFileDirectoryBlock entry = Assert.Single(new MfsVolume(new MemoryStream(image)).GetEntries());
        DateTime recorded = entry.CreationDate.ToDateTime();

        // Stored with whole-second precision, so allow for truncation.
        Assert.InRange(recorded, before.AddSeconds(-1), after);
    }

    // ---- Allocation-free entry enumeration --------------------------------------------

    [Fact]
    public void GetEntries_CanBeEnumeratedRepeatedlyAndThroughEveryEnumerationPattern()
    {
        var volume = new MfsVolume(new MemoryStream(CreateImage(w =>
        {
            for (int i = 0; i < 20; i++)
            {
                w.AddFile($"File {i}", "TEXT", "ttxt", [(byte)i]);
            }
        })));
        MfsDirectoryEntries entries = volume.GetEntries();

        int viaForeach = 0;
        foreach (MfsFileDirectoryBlock _ in entries)
        {
            viaForeach++;
        }

        Assert.Equal(20, viaForeach);
        Assert.Equal(20, entries.Count());                                    // generic interface
        Assert.Equal(20, ((System.Collections.IEnumerable)entries).Cast<object>().Count()); // non-generic
        Assert.Equal(20, volume.GetEntries().ToList().Count);                 // fresh enumeration
    }

    [Fact]
    public void GetEntries_Enumerator_ResetRestartsFromTheFirstEntry()
    {
        var volume = new MfsVolume(new MemoryStream(CreateImage(w =>
        {
            w.AddFile("first", "TEXT", "ttxt", [1]);
            w.AddFile("second", "TEXT", "ttxt", [2]);
        })));

        MfsDirectoryEntries.Enumerator enumerator = volume.GetEntries().GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.True(enumerator.MoveNext());
        Assert.False(enumerator.MoveNext());
        Assert.Equal(default, enumerator.Current);

        enumerator.Reset();
        Assert.True(enumerator.MoveNext());
        Assert.Equal("first", enumerator.Current.Name.ToString());
        Assert.Equal("first", ((System.Collections.IEnumerator)enumerator).Current is MfsFileDirectoryBlock b
            ? b.Name.ToString()
            : null);
        enumerator.Dispose();
    }

    [Fact]
    public void GetEntries_DefaultValue_ThrowsInvalidOperationException()
    {
        MfsDirectoryEntries entries = default;

        Assert.Throws<InvalidOperationException>(() => entries.GetEnumerator());
    }

    [Fact]
    public void GetEntries_SpansMultipleDirectorySectors()
    {
        // Enough entries that the directory occupies several sectors.
        var volume = new MfsVolume(new MemoryStream(CreateImage(w =>
        {
            for (int i = 0; i < 100; i++)
            {
                w.AddFile($"A reasonably long file name number {i:000}", "TEXT", "ttxt", [(byte)i]);
            }
        })));

        Assert.True(volume.MasterDirectoryBlock.FileDirectoryLength > 1);
        Assert.Equal(100, volume.GetEntries().Count());
    }

    // ---- Timestamps (integer tick arithmetic) ------------------------------------------

    [Fact]
    public void Timestamp_EpochTicksConstantMatchesTheMacEpoch()
    {
        Assert.Equal(MfsTimestamp.MacOSEpochTicks, new DateTime(1904, 1, 1).Ticks);
    }

    [Fact]
    public void Timestamp_FromDateTime_AcceptsTheFullUInt32RangeAndRoundTrips()
    {
        var first = new DateTime(1904, 1, 1);
        var last = new DateTime(1904, 1, 1).AddSeconds(uint.MaxValue);

        Assert.Equal(0u, MfsTimestamp.FromDateTime(first).Value);
        Assert.Equal(uint.MaxValue, MfsTimestamp.FromDateTime(last).Value);
        Assert.Equal(last, new MfsTimestamp(uint.MaxValue).ToDateTime());
        Assert.Throws<ArgumentOutOfRangeException>("dateTime", () => MfsTimestamp.FromDateTime(last.AddSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>("dateTime", () => MfsTimestamp.FromDateTime(first.AddTicks(-1)));
    }

    [Fact]
    public void Timestamp_FromDateTime_TruncatesSubSecondPrecision()
    {
        var withFraction = new DateTime(2000, 1, 1, 0, 0, 0).AddMilliseconds(999);

        Assert.Equal(
            MfsTimestamp.FromDateTime(new DateTime(2000, 1, 1, 0, 0, 0)),
            MfsTimestamp.FromDateTime(withFraction));
    }

    // ---- Allocation block map validation ----------------------------------------------

    [Fact]
    public void AllocationBlockMap_Validation_IgnoresReservedFirstTwoEntriesAndChecksTheRest()
    {
        // Entries 0 and 1 aren't block entries, so their (unencoded) values are never written.
        _ = new MfsAllocationBlockMap([0xFFFF, 0xFFFF, 1]);

        Assert.Throws<InvalidDataException>(() => new MfsAllocationBlockMap([0, 0, 1, 0x0FFF, 0x1000]));
        Assert.Throws<InvalidDataException>(() => new MfsAllocationBlockMap([0, 0, 0xFFFF]));
        _ = new MfsAllocationBlockMap([0, 0, 0x0FFF]);
    }

    // ---- Writer name comparison -------------------------------------------------------

    [Fact]
    public void DiskWriter_DuplicateDetection_HasNoFalsePositivesOrMisses()
    {
        var writer = new MfsDiskWriter();
        for (int i = 0; i < 500; i++)
        {
            writer.AddFile($"File {i}", "TEXT", "ttxt");
        }

        // Every name is already present, whatever its case.
        for (int i = 0; i < 500; i += 7)
        {
            Assert.Throws<ArgumentException>("name", () => writer.AddFile($"FILE {i}", "TEXT", "ttxt"));
            Assert.Throws<ArgumentException>("name", () => writer.AddFile($"file {i}", "TEXT", "ttxt"));
        }

        // A name that merely resembles an existing one is fine.
        writer.AddFile("File 500", "TEXT", "ttxt");
        writer.AddFile("File 5 ", "TEXT", "ttxt");
    }

    // ---- Writer internals ---------------------------------------------------------------

    [Fact]
    public void DiskWriter_EmptyForkArray_IsTreatedLikeAMissingFork()
    {
        var writer = new MfsDiskWriter();
        writer.AddFile("none", "TEXT", "ttxt");
        writer.AddFile("empty", "TEXT", "ttxt", [], []);
        writer.AddFile("data", "TEXT", "ttxt", [1]);

        using var stream = new MemoryStream();
        writer.WriteTo(stream);
        stream.Position = 0;
        var volume = new MfsVolume(stream);
        List<MfsFileDirectoryBlock> entries = volume.GetEntries().ToList();

        foreach (MfsFileDirectoryBlock entry in entries.Take(2))
        {
            Assert.Equal(0u, entry.DataForkSize);
            Assert.Equal(0, entry.DataForkAllocationBlock);
            Assert.Equal(0u, entry.ResourceForkSize);
            Assert.Equal(0, entry.ResourceForkAllocationBlock);
        }

        // The empty files take no blocks, so the data fork starts at the first block.
        Assert.Equal(2, entries[2].DataForkAllocationBlock);
    }

    [Theory]
    [InlineData(8192u, 5000, 40)]     // padding (3192) fits in one chunk
    [InlineData(65536u, 5000, 40)]    // padding (60536) spans several 4096-byte chunks
    [InlineData(65536u, 65536, 65536)] // exact multiples need no padding
    [InlineData(1536u, 3073, 1)]      // block size that isn't a power of two
    public void DiskWriter_LargeAllocationBlocks_PadEachForkToWholeBlocks(
        uint blockSize,
        int dataLength,
        int resourceLength)
    {
        byte[] data = new byte[dataLength];
        byte[] resource = new byte[resourceLength];
        new Random(5).NextBytes(data);
        new Random(6).NextBytes(resource);
        var writer = new MfsDiskWriter();
        writer.AddFile("first", "TEXT", "ttxt", data, resource);
        writer.AddFile("second", "TEXT", "ttxt", [1, 2, 3]);

        using var stream = new MemoryStream();
        writer.WriteTo(stream, allocationBlockSize: blockSize);
        byte[] image = stream.ToArray();
        stream.Position = 0;
        var volume = new MfsVolume(stream);
        List<MfsFileDirectoryBlock> entries = volume.GetEntries().ToList();

        Assert.Equal(data, volume.GetDataForkData(entries[0]));
        Assert.Equal(resource, volume.GetResourceForkData(entries[0]));
        Assert.Equal([1, 2, 3], volume.GetDataForkData(entries[1]));

        // The allocated size is a whole number of blocks and the region is padded to it.
        Assert.Equal(0u, entries[0].DataForkAllocatedSize % blockSize);
        long allocationOffset = volume.MasterDirectoryBlock.AllocationBlockStart * 512L;
        long expectedLength = allocationOffset
            + volume.MasterDirectoryBlock.NumberOfAllocationBlocks * (long)blockSize;
        Assert.Equal(expectedLength, image.Length);

        // Padding after the data fork is zero.
        long dataEnd = allocationOffset + dataLength;
        long paddedEnd = allocationOffset + entries[0].DataForkAllocatedSize;
        Assert.All(image.AsSpan((int)dataEnd, (int)(paddedEnd - dataEnd)).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void AllocationBlockMap_WriteTo_MatchesTheDocumentedPackingAndIgnoresDestinationContents()
    {
        var map = new MfsAllocationBlockMap([0, 0, 0xABC, 0x123, 0x456]);

        byte[] clean = new byte[8];
        byte[] dirty = new byte[8];
        Array.Fill(dirty, (byte)0xFF);

        Assert.Equal(5, map.WriteTo(clean));
        Assert.Equal(5, map.WriteTo(dirty));

        // [ABC][123][456] packs to AB C1 23 45 60. The unused low nibble of the last byte is zero.
        Assert.Equal(new byte[] { 0xAB, 0xC1, 0x23, 0x45, 0x60 }, clean[..5]);
        Assert.Equal(clean[..5], dirty[..5]);
        Assert.Equal(0xFF, dirty[5]); // bytes beyond the map are left alone
    }

    [Fact]
    public void AllocationBlockMap_WriteTo_RoundTripsThroughTheVolumeReader()
    {
        // Odd and even block counts, spanning more than one sector of map.
        foreach (int blocks in new[] { 0, 1, 2, 3, 297, 298, 299, 300, 1000 })
        {
            var writer = new MfsDiskWriter();
            if (blocks > 0)
            {
                writer.AddFile("f", "TEXT", "ttxt", new byte[blocks * 512]);
            }

            using var stream = new MemoryStream();
            writer.WriteTo(stream, allocationBlockSize: 512);
            stream.Position = 0;
            var volume = new MfsVolume(stream);

            Assert.Equal(blocks, volume.MasterDirectoryBlock.NumberOfAllocationBlocks);
            for (int block = 2; block < blocks + 1; block++)
            {
                Assert.Equal(block + 1, volume.AllocationBlockMap.GetNextAllocationBlock(block));
            }

            if (blocks > 0)
            {
                Assert.Equal(1, volume.AllocationBlockMap.GetNextAllocationBlock(blocks + 1));
            }
        }
    }

    // ---- Helpers ----------------------------------------------------------------------

    private static byte[] CreateImage(Action<MfsDiskWriter> configure)
    {
        var writer = new MfsDiskWriter();
        configure(writer);
        using var stream = new MemoryStream();
        writer.WriteTo(stream, "Test");
        return stream.ToArray();
    }

    // drDirSt is at MDB offset 14; the file directory starts at that sector.
    private static int DirectoryOffset(byte[] image) =>
        BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(MdbOffset + 14)) * VolumeLayout.SectorSize;
}
