using System.Buffers.Binary;
using MfsReader.Utilities;

namespace MfsReader;

/// <summary>
/// Builds and writes an MFS disk image to a stream.
/// </summary>
public sealed class MfsDiskWriter
{
    private readonly List<FileDefinition> _files = new();

    /// <summary>
    /// Adds a file to the disk image.
    /// </summary>
    /// <param name="name">The file name (at most 255 characters).</param>
    /// <param name="fileType">The 4-character file type code (e.g., "TEXT", "APPL").</param>
    /// <param name="creator">The 4-character creator code.</param>
    /// <param name="dataFork">The data fork contents, or null if the file has no data fork.</param>
    /// <param name="resourceFork">The resource fork contents, or null if the file has no resource fork.</param>
    /// <param name="flags">The file directory block flags.</param>
    /// <param name="finderFlags">The Finder flags.</param>
    /// <param name="folderNumber">The folder number (0 = root, -2 = desktop, -3 = trash).</param>
    public void AddFile(
        string name,
        string fileType,
        string creator,
        byte[]? dataFork = null,
        byte[]? resourceFork = null,
        MfsFileDirectoryBlockFlags flags = MfsFileDirectoryBlockFlags.EntryUsed,
        ushort finderFlags = 0,
        short folderNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fileType);
        ArgumentNullException.ThrowIfNull(creator);

        if (name.Length == 0 || name.Length > String255.Size)
        {
            throw new ArgumentException($"File name must be between 1 and {String255.Size} characters long.", nameof(name));
        }

        if (fileType.Length != String4.Size)
        {
            throw new ArgumentException($"File type must be exactly {String4.Size} characters long.", nameof(fileType));
        }

        if (creator.Length != String4.Size)
        {
            throw new ArgumentException($"Creator must be exactly {String4.Size} characters long.", nameof(creator));
        }

        _files.Add(new FileDefinition(name, fileType, creator, dataFork ?? [], resourceFork ?? [], flags, finderFlags, folderNumber));
    }

    /// <summary>
    /// Writes the complete MFS disk image to the specified stream.
    /// </summary>
    /// <param name="stream">The destination stream. Must be writable.</param>
    /// <param name="volumeName">The volume name (at most 27 characters).</param>
    /// <param name="allocationBlockSize">The allocation block size in bytes. Must be a multiple of 512.</param>
    public void WriteTo(Stream stream, string volumeName = "Untitled", uint allocationBlockSize = 1024)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("Stream must be writable.", nameof(stream));
        }

        if (_files.Count == 0)
        {
            throw new InvalidOperationException("At least one file must be added before writing.");
        }

        if (allocationBlockSize == 0 || (allocationBlockSize % 512) != 0)
        {
            throw new ArgumentException("Allocation block size must be a positive multiple of 512.", nameof(allocationBlockSize));
        }

        // Calculate the number of allocation blocks needed for each fork.
        int totalAllocationBlocks = 0;
        var fileBlockCounts = new (int dataBlocks, int resourceBlocks)[_files.Count];
        for (int i = 0; i < _files.Count; i++)
        {
            int dataBlocks = (_files[i].DataFork.Length > 0)
                ? (int)((_files[i].DataFork.Length + allocationBlockSize - 1) / allocationBlockSize)
                : 0;
            int resourceBlocks = (_files[i].ResourceFork.Length > 0)
                ? (int)((_files[i].ResourceFork.Length + allocationBlockSize - 1) / allocationBlockSize)
                : 0;
            fileBlockCounts[i] = (dataBlocks, resourceBlocks);
            totalAllocationBlocks += dataBlocks + resourceBlocks;
        }

        // Calculate allocation block map size.
        // Each entry is 12 bits. Total bytes = ceil(totalAllocationBlocks * 1.5)
        int abmBytesNeeded = (totalAllocationBlocks * 3 + 1) / 2;

        // The ABM starts at offset 64 (after MDB) within sector 2.
        // First sector has 512 - 64 = 448 bytes available for ABM.
        int abmBytesInFirstSector = Math.Min(448, abmBytesNeeded);
        int abmBytesRemaining = abmBytesNeeded - abmBytesInFirstSector;
        int additionalAbmSectors = (abmBytesRemaining + 511) / 512;

        // MDB is at sector 2. ABM continues into sectors 3, 4, ...
        // Total MDB + ABM sectors = 1 + additionalAbmSectors
        int mdbAndAbmSectors = 1 + additionalAbmSectors;

        // File directory starts after boot blocks (2 sectors) + MDB/ABM sectors.
        ushort fileDirectoryStart = (ushort)(2 + mdbAndAbmSectors);

        // Calculate file directory size.
        // Each entry is 51 + nameLength bytes, word-aligned, entries don't cross block boundaries.
        ushort fileDirectoryLength = CalculateFileDirectoryLength();

        // Allocation blocks start after the file directory.
        ushort allocationBlockStart = (ushort)(fileDirectoryStart + fileDirectoryLength);

        // Build the allocation block map entries.
        // Entries array is indexed 0..totalAllocationBlocks+1.
        // Entries 0 and 1 are special (unused/end-of-chain markers).
        // Actual allocation blocks start at index 2.
        var abmEntries = new ushort[totalAllocationBlocks + 2];
        ushort currentBlock = 2; // First allocation block number

        var fileAllocations = new (ushort dataStart, ushort resourceStart)[_files.Count];
        for (int i = 0; i < _files.Count; i++)
        {
            // Data fork chain
            ushort dataStart = 0;
            if (fileBlockCounts[i].dataBlocks > 0)
            {
                dataStart = currentBlock;
                for (int b = 0; b < fileBlockCounts[i].dataBlocks; b++)
                {
                    if (b < fileBlockCounts[i].dataBlocks - 1)
                    {
                        abmEntries[currentBlock] = (ushort)(currentBlock + 1);
                    }
                    else
                    {
                        abmEntries[currentBlock] = 1; // End of chain
                    }
                    currentBlock++;
                }
            }

            // Resource fork chain
            ushort resourceStart = 0;
            if (fileBlockCounts[i].resourceBlocks > 0)
            {
                resourceStart = currentBlock;
                for (int b = 0; b < fileBlockCounts[i].resourceBlocks; b++)
                {
                    if (b < fileBlockCounts[i].resourceBlocks - 1)
                    {
                        abmEntries[currentBlock] = (ushort)(currentBlock + 1);
                    }
                    else
                    {
                        abmEntries[currentBlock] = 1; // End of chain
                    }
                    currentBlock++;
                }
            }

            fileAllocations[i] = (dataStart, resourceStart);
        }

        var allocationBlockMap = new MfsAllocationBlockMap(abmEntries);

        // Create the MDB.
        var now = MfsTimestamp.FromDateTime(DateTime.UtcNow);
        var mdb = new MfsMasterDirectoryBlock(
            creationDate: now,
            attributes: 0,
            numberOfFiles: (ushort)_files.Count,
            fileDirectoryStart: fileDirectoryStart,
            fileDirectoryLength: fileDirectoryLength,
            numberOfAllocationBlocks: (ushort)totalAllocationBlocks,
            allocationBlockSize: allocationBlockSize,
            clumpSize: allocationBlockSize,
            allocationBlockStart: allocationBlockStart,
            nextFileNumber: (uint)(_files.Count + 1),
            freeAllocationBlocks: 0,
            volumeName: String27.FromString(volumeName));

        Span<byte> block = stackalloc byte[512];

        // Write boot blocks (2 sectors, all zeros).
        block.Clear();
        stream.Write(block);
        stream.Write(block);

        // Write MDB (64 bytes) + ABM start in sector 2.
        block.Clear();
        mdb.WriteTo(block[..MfsMasterDirectoryBlock.Size]);

        // Write ABM into remaining bytes of this sector.
        Span<byte> abmBuffer = stackalloc byte[Math.Max(abmBytesNeeded, 1)];
        abmBuffer.Clear();
        allocationBlockMap.WriteTo(abmBuffer);
        abmBuffer[..abmBytesInFirstSector].CopyTo(block[MfsMasterDirectoryBlock.Size..]);
        stream.Write(block);

        // Write additional ABM sectors if needed.
        int abmOffset = abmBytesInFirstSector;
        for (int s = 0; s < additionalAbmSectors; s++)
        {
            block.Clear();
            int bytesToCopy = Math.Min(512, abmBytesNeeded - abmOffset);
            abmBuffer.Slice(abmOffset, bytesToCopy).CopyTo(block);
            stream.Write(block);
            abmOffset += bytesToCopy;
        }

        // Write file directory entries.
        WriteFileDirectory(stream, fileAllocations, allocationBlockSize, now, block);

        // Write allocation block data.
        WriteAllocationBlocks(stream, allocationBlockSize, block);
    }

    private ushort CalculateFileDirectoryLength()
    {
        int currentBlockOffset = 0;
        int sectorsNeeded = 1;

        for (int i = 0; i < _files.Count; i++)
        {
            int entrySize = MfsFileDirectoryBlock.MinSize + _files[i].Name.Length;
            // Word-align
            if ((entrySize & 1) != 0)
            {
                entrySize++;
            }

            // Check if entry fits in current block.
            if (currentBlockOffset + entrySize > 512)
            {
                // Move to next block.
                sectorsNeeded++;
                currentBlockOffset = 0;
            }

            currentBlockOffset += entrySize;
        }

        return (ushort)sectorsNeeded;
    }

    private void WriteFileDirectory(
        Stream stream,
        (ushort dataStart, ushort resourceStart)[] fileAllocations,
        uint allocationBlockSize,
        MfsTimestamp timestamp,
        Span<byte> block)
    {
        block.Clear();
        int blockOffset = 0;

        for (int i = 0; i < _files.Count; i++)
        {
            var file = _files[i];
            var name = String255.FromString(file.Name);
            int entrySize = MfsFileDirectoryBlock.MinSize + file.Name.Length;
            int alignedEntrySize = entrySize;
            if ((alignedEntrySize & 1) != 0)
            {
                alignedEntrySize++;
            }

            // Check if entry fits in current block.
            if (blockOffset + alignedEntrySize > 512)
            {
                // Write current block and start a new one.
                stream.Write(block);
                block.Clear();
                blockOffset = 0;
            }

            uint dataAllocatedSize = file.DataFork.Length > 0
                ? (uint)(((file.DataFork.Length + allocationBlockSize - 1) / allocationBlockSize) * allocationBlockSize)
                : 0;
            uint resourceAllocatedSize = file.ResourceFork.Length > 0
                ? (uint)(((file.ResourceFork.Length + allocationBlockSize - 1) / allocationBlockSize) * allocationBlockSize)
                : 0;

            var entry = new MfsFileDirectoryBlock(
                flags: file.Flags,
                fileType: String4.FromString(file.FileType),
                creator: String4.FromString(file.Creator),
                finderFlags: file.FinderFlags,
                folderNumber: file.FolderNumber,
                fileNumber: (uint)(i + 1),
                dataForkAllocationBlock: fileAllocations[i].dataStart,
                dataForkSize: (uint)file.DataFork.Length,
                dataForkAllocatedSize: dataAllocatedSize,
                resourceForkAllocationBlock: fileAllocations[i].resourceStart,
                resourceForkSize: (uint)file.ResourceFork.Length,
                resourceForkAllocatedSize: resourceAllocatedSize,
                creationDate: timestamp,
                lastModificationDate: timestamp,
                name: name);

            entry.WriteTo(block[blockOffset..], out _);
            blockOffset += alignedEntrySize;
        }

        // Write the final directory block.
        stream.Write(block);
    }

    private void WriteAllocationBlocks(Stream stream, uint allocationBlockSize, Span<byte> block)
    {
        for (int i = 0; i < _files.Count; i++)
        {
            WriteForkData(stream, _files[i].DataFork, allocationBlockSize, block);
            WriteForkData(stream, _files[i].ResourceFork, allocationBlockSize, block);
        }
    }

    private static void WriteForkData(Stream stream, byte[] data, uint allocationBlockSize, Span<byte> block)
    {
        if (data.Length == 0)
        {
            return;
        }

        // Write the fork data, padding the last block to allocationBlockSize.
        int offset = 0;
        while (offset < data.Length)
        {
            int bytesToWrite = (int)Math.Min(allocationBlockSize, data.Length - offset);
            stream.Write(data, offset, bytesToWrite);

            // Pad remaining bytes in this allocation block.
            int padding = (int)allocationBlockSize - bytesToWrite;
            if (padding > 0)
            {
                block.Clear();
                // Write padding in 512-byte chunks.
                while (padding > 0)
                {
                    int chunk = Math.Min(512, padding);
                    stream.Write(block[..chunk]);
                    padding -= chunk;
                }
            }

            offset += bytesToWrite;
        }
    }

    private sealed record FileDefinition(
        string Name,
        string FileType,
        string Creator,
        byte[] DataFork,
        byte[] ResourceFork,
        MfsFileDirectoryBlockFlags Flags,
        ushort FinderFlags,
        short FolderNumber);
}
