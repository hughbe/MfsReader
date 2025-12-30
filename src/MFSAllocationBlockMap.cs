using System.Buffers;

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
        int bytesFromMdb = initialData.Length - MFSMasterDirectoryBlock.Size;
        int bytesCopied = Math.Min(bytesFromMdb, totalBytesNeeded);
        
        // Use ArrayPool for the temporary buffer to avoid heap allocation
        byte[]? rentedBuffer = null;
        Span<byte> allocationMapData = totalBytesNeeded <= 1024
            ? stackalloc byte[totalBytesNeeded]
            : (rentedBuffer = ArrayPool<byte>.Shared.Rent(totalBytesNeeded)).AsSpan(0, totalBytesNeeded);
        
        try
        {
            initialData.Slice(MFSMasterDirectoryBlock.Size, bytesCopied).CopyTo(allocationMapData);
            
            // Read additional blocks if needed
            int bytesRemaining = totalBytesNeeded - bytesCopied;
            int destOffset = bytesCopied;
            int blockIndex = 1; // Next block after MDB
            Span<byte> blockBuffer = stackalloc byte[512];
            
            while (bytesRemaining > 0)
            {
                volume.Stream.Seek(
                    volume.StreamStartOffset + MFSVolume.MasterDirectoryBlockOffset + blockIndex * 512,
                    SeekOrigin.Begin);
                
                if (volume.Stream.Read(blockBuffer) != blockBuffer.Length)
                {
                    throw new InvalidDataException("Unable to read DSK allocation block map.");
                }
                
                int bytesToCopy = Math.Min(512, bytesRemaining);
                blockBuffer.Slice(0, bytesToCopy).CopyTo(allocationMapData.Slice(destOffset));
                
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
