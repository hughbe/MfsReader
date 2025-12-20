using System.Diagnostics;

namespace MfsReader;

/// <summary>
/// Represents the allocation block map of an MFS volume.
/// </summary>
public struct MFSAllocationBlockMap
{
    /// <summary>
    /// Gets the allocation block entries.
    /// </summary>
    public ushort[] Entries { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MFSAllocationBlockMap"/> struct.
    /// </summary>
    /// <param name="initialData">The initial data containing the allocation block map.</param>
    /// <param name="volume">The MFS volume.</param>
    /// <exception cref="InvalidDataException">Thrown if the allocation block map cannot be read.</exception>
    public MFSAllocationBlockMap(Span<byte> initialData, MFSVolume volume)
    {
        // The data for the allocation block map starts immediately
        // after the volume information in the master directory block.
        int currentBlock = 0;
        int currentOffsetInBlock = MFSMasterDirectoryBlock.Size;

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
        Span<byte> blockBuffer = stackalloc byte[512];
        byte GetByte(Span<byte> initialData, Span<byte> blockBuffer, long offset)
        {
            if (currentBlock == 0)
            {
                if (offset < initialData.Length)
                {
                    return initialData[(int)offset];
                }
            }
            else
            {
                if (offset < blockBuffer.Length)
                {
                    return blockBuffer[(int)offset];
                }
            }

            // Read the next block from the stream.
            currentBlock++;
            currentOffsetInBlock = 0;
            volume.Stream.Seek(
                volume.StreamStartOffset + MFSVolume.MasterDirectoryBlockOffset + currentBlock * 512,
                SeekOrigin.Begin);
            if (volume.Stream.Read(blockBuffer) != blockBuffer.Length)
            {
                throw new InvalidDataException("Unable to read DSK allocation block map.");
            }

            return blockBuffer[currentOffsetInBlock++];
        }

        var entries = new ushort[volume.MasterDirectoryBlock.NumberOfAllocationBlocks + 2];
        for (int i = 2; i < entries.Length; i++)
        {
            var offset = currentOffsetInBlock;
            var byte1 = GetByte(initialData, blockBuffer, currentOffsetInBlock++);
            var byte2 = GetByte(initialData, blockBuffer, currentOffsetInBlock);

            // Each entry is 12 bits.
            if (i % 2 == 0)
            {
                // Even entry - starts at the current byte.
                // Takes 12 bits: all 8 bits of byte1 and the high 4 bits of byte2.
                entries[i] = (ushort)((byte1 << 4) | ((byte2 >> 4) & 0x0F));
            }
            else
            {
                // Odd entry - starts at the high nibble of the current byte.
                // Takes 12 bits: the low 4 bits of byte1 and all 8 bits of byte2.
                entries[i] = (ushort)(((byte1 & 0x0F) << 8) | byte2);

                // Move to the next byte after reading an odd entry.
                currentOffsetInBlock++;
            }
        }

        Entries = entries;
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
}
