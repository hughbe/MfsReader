using System.Buffers;

namespace MfsReader;

/// <summary>
/// Represents the allocation block map of an MFS volume.
/// </summary>
public struct MfsAllocationBlockMap
{
    /// <summary>
    /// Gets the allocation block entries.
    /// </summary>
    public ushort[] Entries { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MfsAllocationBlockMap"/> struct.
    /// </summary>
    /// <param name="initialData">The initial data containing the allocation block map.</param>
    /// <param name="volume">The MFS volume.</param>
    /// <exception cref="InvalidDataException">Thrown if the allocation block map cannot be read.</exception>
    public MfsAllocationBlockMap(Span<byte> initialData, MfsVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (initialData.Length < MfsMasterDirectoryBlock.Size)
        {
            throw new ArgumentException(
                $"Initial data must contain at least {MfsMasterDirectoryBlock.Size} bytes.",
                nameof(initialData));
        }

        // The data for the allocation block map starts immediately
        // after the volume information in the master directory block.

        // From https://www.weihenstephan.org/~michaste/pagetable/mac/Inside_Macintosh.pdf
        // "The volume allocation block map represents every allocation
        // block on the volume with a 12-bit entry indicating whether
        // the block is unused or allocated to a file. It begins in the
        // master directory block at the byte following the volume information,
        // and continues for as many logical blocks as needed.
        // The first entry in the block map is for block number 2.
        // The block map doesn't contain entries for the system startup
        // blocks. Each entry specifies whether the block is unused,
        // whether it's the last block in the file, or which allocation
        // block is next in the file"

        // Read all allocation block map data into a contiguous buffer.
        // The map spans multiple 512-byte logical blocks starting from the MDB.
        // Calculate total bytes needed: 1.5 bytes per entry (12 bits each)
        int numEntries = volume.MasterDirectoryBlock.NumberOfAllocationBlocks;
        int totalBytesNeeded = (numEntries * 3 + 1) / 2; // Round up for 12-bit entries

        // The allocation map starts at offset 64 within the MDB block.
        // First, copy the remaining bytes from the initial MDB block (offsets 64-511).
        int bytesFromMdb = initialData.Length - MfsMasterDirectoryBlock.Size;
        int bytesCopied = Math.Min(bytesFromMdb, totalBytesNeeded);

        // Use ArrayPool for the temporary buffer to avoid heap allocation
        byte[]? rentedBuffer = null;
        Span<byte> allocationMapData = totalBytesNeeded <= 1024
            ? stackalloc byte[totalBytesNeeded]
            : (rentedBuffer = ArrayPool<byte>.Shared.Rent(totalBytesNeeded)).AsSpan(0, totalBytesNeeded);

        try
        {
            initialData.Slice(MfsMasterDirectoryBlock.Size, bytesCopied).CopyTo(allocationMapData);

            // Read additional blocks if needed
            int bytesRemaining = totalBytesNeeded - bytesCopied;
            int destOffset = bytesCopied;
            int blockIndex = 1; // Next block after MDB
            Span<byte> blockBuffer = stackalloc byte[VolumeLayout.SectorSize];

            while (bytesRemaining > 0)
            {
                volume.Stream.Seek(
                    volume.StreamStartOffset + MfsVolume.MasterDirectoryBlockOffset + blockIndex * 512,
                    SeekOrigin.Begin);
                volume.Stream.ReadExactly(blockBuffer);

                int bytesToCopy = Math.Min(VolumeLayout.SectorSize, bytesRemaining);
                blockBuffer[..bytesToCopy].CopyTo(allocationMapData[destOffset..]);

                destOffset += bytesToCopy;
                bytesRemaining -= bytesToCopy;
                blockIndex++;
            }

            // Now parse the contiguous allocation map data
            var entries = new ushort[numEntries + 2];
            int byteOffset = 0;

            for (int i = 2; i < entries.Length; i++)
            {
                byte byte1 = allocationMapData[byteOffset];
                byte byte2 = allocationMapData[byteOffset + 1];

                // Each entry is 12 bits.
                if (i % 2 == 0)
                {
                    // Even entry - starts at the current byte.
                    // Takes 12 bits: all 8 bits of byte1 and the high 4 bits of byte2.
                    entries[i] = (ushort)((byte1 << 4) | ((byte2 >> 4) & 0x0F));
                    byteOffset++;
                }
                else
                {
                    // Odd entry - starts at the high nibble of the current byte.
                    // Takes 12 bits: the low 4 bits of byte1 and all 8 bits of byte2.
                    entries[i] = (ushort)(((byte1 & 0x0F) << 8) | byte2);

                    // Move past both bytes after reading an odd entry.
                    byteOffset += 2;
                }

                // Entry values are deliberately not range-checked here. Unreferenced
                // entries may hold reserved or stale values (e.g. 0xFFF), and rejecting
                // them would make every other file on the volume unreadable. Pointers
                // are validated when a chain is followed (see MfsVolume).
            }

            Entries = entries;
        }
        finally
        {
            if (rentedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MfsAllocationBlockMap"/> struct from the given entries.
    /// </summary>
    /// <param name="entries">The allocation block map entries.</param>
    public MfsAllocationBlockMap(ushort[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Length < 2 || entries.Length > 4096)
        {
            throw new ArgumentException("Allocation block map must contain between 2 and 4096 entries.", nameof(entries));
        }

        // Snapshot first, then validate the snapshot, so the caller can't change the
        // array between the check and the copy.
        ushort[] snapshot = entries.ToArray();
        ValidateEntries(snapshot);
        Entries = snapshot;
    }

    /// <summary>
    /// Writes the allocation block map to the specified span, encoding entries as 12-bit values.
    /// </summary>
    /// <param name="data">The destination span. Must be large enough to hold all encoded entries.</param>
    /// <returns>The number of bytes written.</returns>
    public readonly int WriteTo(Span<byte> data) =>
        // Entries is a public array and may have been modified since construction, so
        // Encode validates it again.
        Encode(Entries, data);

    /// <summary>
    /// Encodes block entries (starting at block 2) as packed 12-bit values.
    /// </summary>
    /// <param name="entries">The map entries, indexed by block number. Entries 0 and 1 are not encoded.</param>
    /// <param name="data">The destination span.</param>
    /// <returns>The number of bytes written.</returns>
    /// <remarks>
    /// Every byte written is fully determined by the entries, so the destination does not
    /// need to be cleared first. If the number of block entries is odd, the unused low
    /// nibble of the final byte is written as zero.
    /// </remarks>
    internal static int Encode(ReadOnlySpan<ushort> entries, Span<byte> data)
    {
        ReadOnlySpan<ushort> blocks = entries[2..];
        int bytesRequired = (blocks.Length * 3 + 1) / 2;
        if (data.Length < bytesRequired)
        {
            throw new ArgumentException($"Destination must be at least {bytesRequired} bytes long.", nameof(data));
        }

        ValidateEntries(entries);

        // Entries are packed in pairs: [aaaaaaaa][aaaabbbb][bbbbbbbb]. Writing whole bytes
        // (rather than merging nibbles into existing bytes) needs no reads of the destination.
        int offset = 0;
        int i = 0;
        for (; i + 1 < blocks.Length; i += 2)
        {
            ushort first = blocks[i];
            ushort second = blocks[i + 1];
            data[offset] = (byte)(first >> 4);
            data[offset + 1] = (byte)(((first & 0x0F) << 4) | (second >> 8));
            data[offset + 2] = (byte)second;
            offset += 3;
        }

        if (i < blocks.Length)
        {
            // A trailing entry fills a byte and the high nibble of the next.
            ushort last = blocks[i];
            data[offset] = (byte)(last >> 4);
            data[offset + 1] = (byte)((last & 0x0F) << 4);
            offset += 2;
        }

        return offset;
    }

    /// <summary>
    /// Gets the allocation block entry at the specified index.
    /// </summary>
    /// <param name="index">The index of the allocation block.</param>
    /// <returns>The next allocation block entry.</returns>
    public readonly ushort GetNextAllocationBlock(int index)
    {
        if (index < 0 || index >= Entries.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range of allocation block entries.");
        }

        return Entries[index];
    }

    /// <summary>
    /// The largest value that fits in a 12-bit allocation block map entry.
    /// </summary>
    private const ushort MaxEntryValue = 0x0FFF;

    // Only checks that every value can be encoded in 12 bits. Whether a pointer refers
    // to a real block is checked when a chain is followed. Entries 0 and 1 are not
    // block entries and are skipped. The scan is vectorised and allocation free.
    private static void ValidateEntries(ReadOnlySpan<ushort> entries)
    {
        int bad = entries[2..].IndexOfAnyExceptInRange((ushort)0, MaxEntryValue);
        if (bad >= 0)
        {
            throw CreateEntryTooLargeException(bad + 2, entries[bad + 2]);
        }
    }

    private static InvalidDataException CreateEntryTooLargeException(int index, ushort value) =>
        new($"Allocation block {index} has value {value}, which does not fit in a 12-bit map entry.");
}
