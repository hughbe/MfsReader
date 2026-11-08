using System.Buffers.Binary;
using System.Text;
using MfsReader.Utilities;

namespace MfsReader.Tests;

/// <summary>
/// Regression tests for reading damaged or hostile images, fragmented files, and sharing a
/// volume between threads.
/// </summary>
public class HardeningTests
{
    private const int MdbOffset = MfsVolume.MasterDirectoryBlockOffset;

    // ---- A corrupt fork size must not drive allocation -----------------------------------

    [Theory]
    [InlineData(0x60000000u)] // 1.5 GiB: used to be allocated before the chain was checked
    [InlineData(0x7FFFFFFFu)] // int.MaxValue: used to throw OutOfMemoryException
    [InlineData(5000u)]       // far more than this 3 KiB image can hold, but small
    public void GetDataForkData_WhenSizeExceedsTheVolume_ThrowsInvalidDataExceptionWithoutAllocating(uint claimedSize)
    {
        byte[] image = CreateImage(w => w.AddFile("victim", "TEXT", "ttxt", [1, 2, 3]));
        SetDataForkLength(image, claimedSize);
        var volume = new MfsVolume(new MemoryStream(image));
        MfsFileDirectoryBlock entry = Assert.Single(volume.GetEntries());

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entry));
        Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entry, new MemoryStream()));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1_000_000, $"Allocated {allocated:N0} bytes for a {image.Length}-byte image.");
    }

    [Fact]
    public void GetDataForkData_WhenSizeEqualsTheWholeAllocationArea_StillReads()
    {
        // The capacity check must be a limit, not an off-by-one: a fork may fill the volume.
        byte[] data = new byte[4 * 512];
        new Random(1).NextBytes(data);
        byte[] image = CreateImage(w => w.AddFile("full", "TEXT", "ttxt", data), blockSize: 512);
        var volume = new MfsVolume(new MemoryStream(image));
        MfsFileDirectoryBlock entry = Assert.Single(volume.GetEntries());

        Assert.Equal(4, volume.MasterDirectoryBlock.NumberOfAllocationBlocks);
        Assert.Equal(data, volume.GetDataForkData(entry));
    }

    // ---- Concurrent use ------------------------------------------------------------------

    [Fact]
    public async Task Volume_ReadFromManyThreads_ReturnsCorrectDataEveryTime()
    {
        var expected = new Dictionary<string, byte[]>();
        byte[] image = CreateImage(w =>
        {
            var rng = new Random(11);
            for (int i = 0; i < 12; i++)
            {
                byte[] data = new byte[rng.Next(1, 6000)];
                rng.NextBytes(data);
                expected[$"File {i}"] = data;
                w.AddFile($"File {i}", "TEXT", "ttxt", data, [(byte)i]);
            }
        });
        var volume = new MfsVolume(new MemoryStream(image));
        List<MfsFileDirectoryBlock> entries = volume.GetEntries().ToList();

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Task[] workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int pass = 0; pass < 60; pass++)
            {
                foreach (MfsFileDirectoryBlock entry in entries)
                {
                    string name = entry.Name.ToString();
                    if (!volume.GetDataForkData(entry).AsSpan().SequenceEqual(expected[name]))
                    {
                        failures.Add($"worker {worker}: data of {name} was wrong");
                    }

                    using var sink = new MemoryStream();
                    volume.GetResourceForkData(entry, sink);
                    if (sink.Length != 1)
                    {
                        failures.Add($"worker {worker}: resource of {name} was wrong");
                    }
                }

                // Directory enumeration shares the stream too.
                if (volume.GetEntries().Count() != entries.Count)
                {
                    failures.Add($"worker {worker}: directory enumeration was wrong");
                }
            }
        })).ToArray();
        await Task.WhenAll(workers);

        Assert.Empty(failures.Take(5));
    }

    // ---- Fragmented and damaged chains ---------------------------------------------------

    [Theory]
    [InlineData(512u, 100 * 512 + 137)]  // many blocks, partial last block
    [InlineData(512u, 250 * 512)]        // one run longer than the 64 KiB transfer buffer
    [InlineData(1536u, 40 * 1536 - 1)]   // block size that isn't a power of two
    [InlineData(4096u, 9 * 4096 + 1)]
    public void Fork_StoredInRandomRuns_ReadsBackIdentically(uint blockSize, int length)
    {
        int blocks = (length + (int)blockSize - 1) / (int)blockSize;
        for (int seed = 0; seed < 40; seed++)
        {
            int[] order = seed switch
            {
                0 => Identity(blocks),
                1 => Identity(blocks).Reverse().ToArray(),
                2 => Shuffled(Identity(blocks), new Random(seed)),
                _ => RandomRuns(blocks, new Random(seed)),
            };

            byte[] image = CreateFragmentedImage(blockSize, length, order, out byte[] original);
            var volume = new MfsVolume(new MemoryStream(image));
            MfsFileDirectoryBlock entry = Assert.Single(volume.GetEntries());

            Assert.True(original.AsSpan().SequenceEqual(volume.GetDataForkData(entry)), $"byte[] read, seed {seed}");
            using var sink = new MemoryStream();
            Assert.Equal(length, volume.GetDataForkData(entry, sink));
            Assert.True(original.AsSpan().SequenceEqual(sink.ToArray()), $"stream read, seed {seed}");
        }
    }

    [Theory]
    [InlineData(new[] { 2, 3, 4 })]
    [InlineData(new[] { 4, 2, 3 })]
    [InlineData(new[] { 3, 4, 2 })]
    public void Fork_WithAllocationBlocksLargerThanTheTransferBuffer_ReadsBackIdentically(int[] order)
    {
        // 128 KiB blocks: a single block is bigger than the 64 KiB transfer buffer.
        byte[] image = CreateFragmentedImage(131072, 2 * 131072 + 5000, order, out byte[] original);
        var volume = new MfsVolume(new MemoryStream(image));
        MfsFileDirectoryBlock entry = Assert.Single(volume.GetEntries());

        Assert.Equal(original, volume.GetDataForkData(entry));
        using var sink = new MemoryStream();
        volume.GetDataForkData(entry, sink);
        Assert.Equal(original, sink.ToArray());
    }

    [Fact]
    public void Fork_WhoseChainEndsEarly_DeliversTheGoodDataBeforeThrowing()
    {
        byte[] image = CreateFragmentedImage(512, 6 * 512, Identity(6), out byte[] original);
        SetMapEntry(image, block: 3, value: 1); // the chain now ends after block 3
        var volume = new MfsVolume(new MemoryStream(image));
        MfsFileDirectoryBlock entry = Assert.Single(volume.GetEntries());

        using var sink = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entry, sink));
        Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entry));

        // The two intact blocks were written before the damage was reported.
        Assert.Equal(original[..1024], sink.ToArray());
    }

    [Fact]
    public void Fork_WhoseChainPointsOutsideTheVolume_DeliversTheGoodDataBeforeThrowing()
    {
        byte[] image = CreateFragmentedImage(512, 6 * 512, Identity(6), out byte[] original);
        SetMapEntry(image, block: 4, value: 0xFFF);
        var volume = new MfsVolume(new MemoryStream(image));
        MfsFileDirectoryBlock entry = Assert.Single(volume.GetEntries());

        using var sink = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entry, sink));

        Assert.Equal(original[..1536], sink.ToArray());
    }

    [Fact]
    public void Fork_WhoseContiguousRunLeadsBackIntoAnEarlierRun_IsReportedAsACycle()
    {
        // 6 -> 7 -> 4 -> 5 -> 6 ...: block 6 is reached again by extending the run from 4.
        byte[] image = CreateFragmentedImage(512, 6 * 512, Identity(6), out _);
        SetFirstBlock(image, 6);
        SetMapEntry(image, 6, 7);
        SetMapEntry(image, 7, 4);
        SetMapEntry(image, 4, 5);
        SetMapEntry(image, 5, 6);
        var volume = new MfsVolume(new MemoryStream(image));
        MfsFileDirectoryBlock entry = Assert.Single(volume.GetEntries());

        using var sink = new MemoryStream();
        var exception = Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entry, sink));

        Assert.Contains("cycle", exception.Message);
        Assert.Equal(4 * 512, sink.Length);
        Assert.Contains("cycle", Assert.Throws<InvalidDataException>(() => volume.GetDataForkData(entry)).Message);
    }

    // ---- Partition maps ------------------------------------------------------------------

    [Fact]
    public void MfsDisk_WhenThePartitionMapHasNoMfsPartition_HasNoVolumes()
    {
        byte[] image = File.ReadAllBytes(Path.Combine("Samples", "APM", "combined.dsk"));
        Assert.NotEmpty(new MfsDisk(new MemoryStream(image)).Volumes);

        byte[] mfs = Encoding.ASCII.GetBytes("Apple_MFS");
        byte[] hfs = Encoding.ASCII.GetBytes("Apple_HFS");
        int replaced = 0;
        for (int i = 0; i + mfs.Length < 16384; i++)
        {
            if (image.AsSpan(i, mfs.Length).SequenceEqual(mfs))
            {
                hfs.CopyTo(image, i);
                replaced++;
            }
        }

        Assert.True(replaced > 0, "The sample is expected to contain an Apple_MFS partition entry.");
        Assert.Empty(new MfsDisk(new MemoryStream(image)).Volumes);
    }

    // ---- Seeded fuzzing --------------------------------------------------------------------

    [Fact]
    public void DamagedImages_OnlyEverThrowInvalidDataExceptionAndNeverAllocateWildly()
    {
        var seeds = new List<(string Name, byte[] Data)>();
        foreach (string file in new[] { "mfs400K.dsk", "SystemStartup.dsk", "MacSpeak.dsk" })
        {
            seeds.Add((file, File.ReadAllBytes(Path.Combine("Samples", file))));
        }

        for (int i = 0; i < 3; i++)
        {
            var writer = new MfsDiskWriter();
            var rng = new Random(i);
            for (int f = 0; f < 6; f++)
            {
                byte[] data = new byte[rng.Next(0, 6000)];
                byte[] resource = new byte[rng.Next(0, 3000)];
                rng.NextBytes(data);
                rng.NextBytes(resource);
                writer.AddFile($"File {f}", "TEXT", "ttxt", data, resource);
            }

            using var stream = new MemoryStream();
            writer.WriteTo(stream, $"Gen{i}", (uint)(512 * (i + 1)));
            seeds.Add(($"generated{i}", stream.ToArray()));
        }

        var random = new Random(20261108);
        var problems = new List<string>();
        for (int iteration = 0; iteration < 12_000 && problems.Count < 5; iteration++)
        {
            (string name, byte[] original) = seeds[random.Next(seeds.Count)];
            (string how, byte[] damaged) = Damage(original, random);

            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                ReadEverything(damaged);
            }
            catch (InvalidDataException)
            {
                // The documented way for a damaged image to fail.
            }
            catch (Exception ex)
            {
                problems.Add($"#{iteration} {name} ({how}): {ex.GetType().Name}: {ex.Message}");
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated > 32_000_000)
            {
                problems.Add($"#{iteration} {name} ({how}): allocated {allocated:N0} bytes for a {damaged.Length:N0}-byte image");
            }
        }

        Assert.Empty(problems);
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static void ReadEverything(byte[] image)
    {
        var disk = new MfsDisk(new MemoryStream(image));
        foreach (MfsVolume volume in disk.Volumes)
        {
            var sink = new CountingStream();
            foreach (MfsFileDirectoryBlock entry in volume.GetEntries())
            {
                _ = entry.Name.ToString();
                _ = entry.FileType.ToString();
                _ = entry.CreationDate.ToDateTime();
                volume.GetDataForkData(entry, sink);
                volume.GetResourceForkData(entry, sink);
                _ = volume.GetDataForkData(entry);
                _ = volume.GetResourceForkData(entry);
            }
        }
    }

    private static (string How, byte[] Image) Damage(byte[] original, Random rng)
    {
        byte[] image = (byte[])original.Clone();
        int directory = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(MdbOffset + 14)) * 512;
        bool hasDirectory = directory + 512 <= image.Length;
        switch (rng.Next(8))
        {
            case 0:
                for (int i = rng.Next(1, 9); i > 0; i--)
                {
                    image[rng.Next(image.Length)] ^= (byte)(1 << rng.Next(8));
                }

                return ("bit flips", image);
            case 1:
                for (int i = rng.Next(1, 6); i > 0; i--)
                {
                    image[MdbOffset + rng.Next(MfsMasterDirectoryBlock.Size)] = (byte)rng.Next(256);
                }

                return ("master directory block bytes", image);
            case 2:
                int[] fields = [2, 6, 10, 12, 14, 16, 18, 20, 24, 28, 30, 34, 38];
                uint[] extremes = [0, 1, 2, 0xFF, 0x7FFF, 0x8000, 0xFFFF, 0xFFFFFFFF];
                BinaryPrimitives.WriteUInt16BigEndian(
                    image.AsSpan(MdbOffset + fields[rng.Next(fields.Length)]),
                    (ushort)extremes[rng.Next(extremes.Length)]);
                return ("master directory block field", image);
            case 3 when hasDirectory:
                for (int i = rng.Next(1, 8); i > 0; i--)
                {
                    image[directory + rng.Next(512)] = (byte)rng.Next(256);
                }

                return ("directory bytes", image);
            case 4 when hasDirectory:
                // Entry fields, including the block numbers and the logical and physical lengths.
                int[] entryFields = [0, 1, 22, 24, 28, 32, 36, 38, 50];
                uint[] values = [0, 1, 0xFF, 0xFFFF, 0x60000000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF];
                int at = directory + entryFields[rng.Next(entryFields.Length)];
                if (rng.Next(2) == 0)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at), values[rng.Next(values.Length)]);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(at), (ushort)values[rng.Next(values.Length)]);
                }

                return ("directory entry field", image);
            case 5:
                for (int i = rng.Next(1, 40); i > 0; i--)
                {
                    image[MdbOffset + 64 + rng.Next(Math.Min(448, image.Length - MdbOffset - 64))] = (byte)rng.Next(256);
                }

                return ("allocation map bytes", image);
            case 6:
                return ("truncation", image[..rng.Next(0, image.Length)]);
            default:
                for (int i = rng.Next(1, 4); i > 0; i--)
                {
                    image[rng.Next(image.Length)] = (byte)rng.Next(256);
                }

                return ("random bytes", image);
        }
    }

    private static byte[] CreateImage(Action<MfsDiskWriter> configure, uint blockSize = 1024)
    {
        var writer = new MfsDiskWriter();
        configure(writer);
        using var stream = new MemoryStream();
        writer.WriteTo(stream, "Test", blockSize);
        return stream.ToArray();
    }

    private static int DirectoryOffset(byte[] image) =>
        BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(MdbOffset + 14)) * VolumeLayout.SectorSize;

    private static void SetDataForkLength(byte[] image, uint length)
    {
        int entry = DirectoryOffset(image);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(entry + 24), length);   // logical
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(entry + 28), length);   // physical
    }

    private static void SetFirstBlock(byte[] image, ushort block) =>
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(DirectoryOffset(image) + 22), block);

    private static void SetMapEntry(byte[] image, int block, ushort value)
    {
        // Entries are packed 12 bits each from block 2, at offset 64 into the master directory sector.
        int index = block - 2;
        int offset = MdbOffset + MfsMasterDirectoryBlock.Size + index * 3 / 2;
        if (index % 2 == 0)
        {
            image[offset] = (byte)(value >> 4);
            image[offset + 1] = (byte)((image[offset + 1] & 0x0F) | ((value & 0x0F) << 4));
        }
        else
        {
            image[offset] = (byte)((image[offset] & 0xF0) | ((value >> 8) & 0x0F));
            image[offset + 1] = (byte)value;
        }
    }

    private static int[] Identity(int blocks) => Enumerable.Range(2, blocks).ToArray();

    private static int[] Shuffled(int[] values, Random rng)
    {
        int[] copy = (int[])values.Clone();
        rng.Shuffle(copy);
        return copy;
    }

    // Physical block numbers for logical blocks 0..n-1: ascending runs of random length, in random order,
    // which is what a fragmented but mostly contiguous real disk looks like.
    private static int[] RandomRuns(int blocks, Random rng)
    {
        var runs = new List<int[]>();
        int next = 2;
        while (next < blocks + 2)
        {
            int length = Math.Min(rng.Next(1, 25), blocks + 2 - next);
            runs.Add(Enumerable.Range(next, length).ToArray());
            next += length;
        }

        return Shuffled([.. Enumerable.Range(0, runs.Count)], rng).SelectMany(i => runs[i]).ToArray();
    }

    /// <summary>
    /// Writes a one-file volume, then moves logical block i of the fork to physical block
    /// <paramref name="order"/>[i] and rewrites the allocation map and directory to match.
    /// </summary>
    private static byte[] CreateFragmentedImage(uint blockSize, int length, int[] order, out byte[] original)
    {
        byte[] data = new byte[length];
        new Random(length).NextBytes(data);
        original = data;
        byte[] contiguous = CreateImage(w => w.AddFile("frag", "TEXT", "ttxt", data), blockSize);

        var volume = new MfsVolume(new MemoryStream(contiguous));
        int blocks = volume.MasterDirectoryBlock.NumberOfAllocationBlocks;
        Assert.Equal(blocks, order.Length);
        Assert.Equal(Enumerable.Range(2, blocks), order.OrderBy(b => b));
        Assert.True(blocks <= 298, "The allocation map must fit in the master directory sector.");

        long dataStart = volume.MasterDirectoryBlock.AllocationBlockStart * (long)VolumeLayout.SectorSize;
        byte[] image = (byte[])contiguous.Clone();
        for (int i = 0; i < blocks; i++)
        {
            Array.Copy(contiguous, dataStart + i * (long)blockSize, image, dataStart + (order[i] - 2) * (long)blockSize, blockSize);
        }

        var entries = new ushort[blocks + 2];
        for (int i = 0; i < blocks; i++)
        {
            entries[order[i]] = i < blocks - 1 ? (ushort)order[i + 1] : (ushort)1;
        }

        MfsAllocationBlockMap.Encode(entries, image.AsSpan(MdbOffset + MfsMasterDirectoryBlock.Size));
        SetFirstBlock(image, (ushort)order[0]);
        return image;
    }

    private sealed class CountingStream : Stream
    {
        public long Total { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Total;
        public override long Position { get => Total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Total += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Total += buffer.Length;
    }
}
