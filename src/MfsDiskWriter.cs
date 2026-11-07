using System.Runtime.InteropServices;
using MfsReader.Utilities;

namespace MfsReader;

/// <summary>
/// Builds and writes an MFS disk image to a stream.
/// </summary>
public sealed class MfsDiskWriter
{
    // Map entries are 12 bits and block numbers start at 2. Inside Macintosh reserves the
    // values 0, 1 and $FFF, so the highest usable block number is $FFE (4094), which gives
    // 4093 blocks. The reader tolerates 4094 for leniency, but never write a chain that
    // points at $FFF.
    private const int MaxWriterAllocationBlocks = 4093;

    private readonly List<FileDefinition> _files = new();

    // MFS file names are unique within a volume and compared without regard to case.
    private readonly HashSet<string> _fileNames = new(StringComparer.OrdinalIgnoreCase);

    // The Mac OS Roman bytes of every file name, back to back. Each file records the offset
    // and length of its name here, so adding a file allocates no per-name buffer and writing
    // the image copies bytes rather than encoding the name a second time.
    private readonly List<byte> _nameBytes = new();

    /// <summary>
    /// Adds a file to the disk image.
    /// </summary>
    /// <param name="name">The file name (at most 255 characters). Must be unique on the volume, ignoring case.</param>
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

        // Encoding validates the name (no nulls, representable in Mac OS Roman, at most 255
        // bytes). The encoded bytes are appended to the shared name buffer below.
        String255 encodedName = EncodeString(name, String255.FromString, nameof(name));
        String4 encodedFileType = EncodeString(fileType, String4.FromString, nameof(fileType));
        String4 encodedCreator = EncodeString(creator, String4.FromString, nameof(creator));

        // Checked last so a failed call (e.g. invalid file type) does not reserve the name.
        if (!_fileNames.Add(name))
        {
            throw new ArgumentException(
                $"A file named '{name}' has already been added. File names must be unique, ignoring case.",
                nameof(name));
        }

        int nameOffset = _nameBytes.Count;
        _nameBytes.AddRange(encodedName.AsReadOnlySpan()[..encodedName.Length]);

        _files.Add(new FileDefinition(
            nameOffset,
            (byte)encodedName.Length,
            encodedFileType,
            encodedCreator,
            Snapshot(dataFork),
            Snapshot(resourceFork),
            flags | MfsFileDirectoryBlockFlags.EntryUsed,
            finderFlags,
            folderNumber));
    }

    // The writer must not observe later changes to the caller's arrays.
    private static byte[] Snapshot(byte[]? data) =>
        data is { Length: > 0 } ? data.AsSpan().ToArray() : [];

    /// <summary>
    /// Writes the complete MFS disk image to the specified stream. If no files have been
    /// added, an empty volume is written.
    /// </summary>
    /// <param name="stream">The destination stream. Must be writable.</param>
    /// <param name="volumeName">The volume name (at most 27 characters).</param>
    /// <param name="allocationBlockSize">The allocation block size in bytes. Must be a multiple of 512.</param>
    public void WriteTo(Stream stream, string volumeName = "Untitled", uint allocationBlockSize = 1024)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(volumeName);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("Stream must be writable.", nameof(stream));
        }

        // A writer with no files produces a valid, empty volume.
        if (allocationBlockSize == 0 || (allocationBlockSize % 512) != 0)
        {
            throw new ArgumentException("Allocation block size must be a positive multiple of 512.", nameof(allocationBlockSize));
        }

        if (allocationBlockSize > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(allocationBlockSize),
                $"Allocation block size must not exceed {int.MaxValue} bytes.");
        }

        if (volumeName.Length == 0 || volumeName.Length > String27.Size)
        {
            throw new ArgumentException($"Volume name must be between 1 and {String27.Size} characters long.", nameof(volumeName));
        }

        String27 encodedVolumeName = EncodeString(volumeName, String27.FromString, nameof(volumeName));

        ReadOnlySpan<FileDefinition> files = CollectionsMarshal.AsSpan(_files);
        ReadOnlySpan<byte> nameBytes = CollectionsMarshal.AsSpan(_nameBytes);
        ValidateFileCount(files.Length);

        // Calculate the number of allocation blocks needed for all forks.
        int totalAllocationBlocks = 0;
        foreach (ref readonly FileDefinition file in files)
        {
            totalAllocationBlocks = checked(
                totalAllocationBlocks
                + GetAllocationBlockCount(file.DataFork.Length, allocationBlockSize)
                + GetAllocationBlockCount(file.ResourceFork.Length, allocationBlockSize));

            if (totalAllocationBlocks > MaxWriterAllocationBlocks)
            {
                throw new InvalidOperationException(
                    $"The files require more than the limit of {MaxWriterAllocationBlocks} allocation blocks. Use a larger allocation block size.");
            }
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
        ushort fileDirectoryStart = checked((ushort)(2 + mdbAndAbmSectors));

        // Calculate file directory size.
        // Each entry is 51 + nameLength bytes, word-aligned, entries don't cross block boundaries.
        (ushort fileDirectoryLength, ushort allocationBlockStart) =
            ValidateDirectoryLayout(fileDirectoryStart, CalculateFileDirectoryLength(files));

        // Build the allocation block map entries on the stack: at most 4093 blocks, so the
        // entries need under 8 KB. Entries 0 and 1 are special (unused/end-of-chain markers);
        // actual allocation blocks start at index 2. Forks are laid out contiguously, each
        // file's data fork followed by its resource fork.
        Span<ushort> abmEntries = stackalloc ushort[totalAllocationBlocks + 2];
        int nextBlock = 2;
        foreach (ref readonly FileDefinition file in files)
        {
            nextBlock = ChainBlocks(abmEntries, nextBlock, GetAllocationBlockCount(file.DataFork.Length, allocationBlockSize));
            nextBlock = ChainBlocks(abmEntries, nextBlock, GetAllocationBlockCount(file.ResourceFork.Length, allocationBlockSize));
        }

        // Create the MDB.
        // Classic Mac volumes record local wall-clock time, not UTC.
        var now = MfsTimestamp.FromDateTime(DateTime.Now);
        var mdb = new MfsMasterDirectoryBlock(
            creationDate: now,
            attributes: 0,
            numberOfFiles: (ushort)files.Length,
            fileDirectoryStart: fileDirectoryStart,
            fileDirectoryLength: fileDirectoryLength,
            numberOfAllocationBlocks: (ushort)totalAllocationBlocks,
            allocationBlockSize: allocationBlockSize,
            clumpSize: allocationBlockSize,
            allocationBlockStart: allocationBlockStart,
            nextFileNumber: (uint)(files.Length + 1),
            freeAllocationBlocks: 0,
            volumeName: encodedVolumeName);

        Span<byte> block = stackalloc byte[512];

        // Write boot blocks (2 sectors, all zeros).
        block.Clear();
        stream.Write(block);
        stream.Write(block);

        // Write MDB (64 bytes) + ABM start in sector 2.
        block.Clear();
        mdb.WriteTo(block[..MfsMasterDirectoryBlock.Size]);

        // Encode the ABM (every byte is written, so the buffer needs no clearing) and place
        // the first part of it in the remaining bytes of this sector.
        Span<byte> abmBuffer = stackalloc byte[Math.Max(abmBytesNeeded, 1)];
        MfsAllocationBlockMap.Encode(abmEntries, abmBuffer);
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
        WriteFileDirectory(stream, files, nameBytes, allocationBlockSize, now, block);

        // Write allocation block data.
        WriteAllocationBlocks(stream, files, allocationBlockSize);
    }

    // Links `count` consecutive blocks starting at `first` into a chain ending in 1, and
    // returns the next free block number.
    private static int ChainBlocks(Span<ushort> abmEntries, int first, int count)
    {
        if (count == 0)
        {
            return first;
        }

        int last = first + count - 1;
        for (int block = first; block < last; block++)
        {
            abmEntries[block] = (ushort)(block + 1);
        }

        abmEntries[last] = 1; // End of chain
        return last + 1;
    }

    private static int CalculateFileDirectoryLength(ReadOnlySpan<FileDefinition> files)
    {
        int currentBlockOffset = 0;
        int sectorsNeeded = 1;

        foreach (ref readonly FileDefinition file in files)
        {
            int entrySize = GetAlignedEntrySize(file.NameLength);

            // Check if entry fits in current block.
            if (currentBlockOffset + entrySize > 512)
            {
                // Move to next block.
                sectorsNeeded++;
                currentBlockOffset = 0;
            }

            currentBlockOffset += entrySize;
        }

        return sectorsNeeded;
    }

    // Entries are 51 bytes plus the name, padded to a whole number of 16-bit words.
    private static int GetAlignedEntrySize(int nameLength) =>
        (MfsFileDirectoryBlock.MinSize + nameLength + 1) & ~1;

    private static void WriteFileDirectory(
        Stream stream,
        ReadOnlySpan<FileDefinition> files,
        ReadOnlySpan<byte> nameBytes,
        uint allocationBlockSize,
        MfsTimestamp timestamp,
        Span<byte> block)
    {
        block.Clear();
        int blockOffset = 0;
        int nextBlock = 2; // mirrors the layout built for the allocation block map

        for (int i = 0; i < files.Length; i++)
        {
            ref readonly FileDefinition file = ref files[i];
            int alignedEntrySize = GetAlignedEntrySize(file.NameLength);

            // Check if entry fits in current block.
            if (blockOffset + alignedEntrySize > 512)
            {
                // Write current block and start a new one.
                stream.Write(block);
                block.Clear();
                blockOffset = 0;
            }

            int dataBlocks = GetAllocationBlockCount(file.DataFork.Length, allocationBlockSize);
            ushort dataStart = dataBlocks > 0 ? (ushort)nextBlock : (ushort)0;
            nextBlock += dataBlocks;

            int resourceBlocks = GetAllocationBlockCount(file.ResourceFork.Length, allocationBlockSize);
            ushort resourceStart = resourceBlocks > 0 ? (ushort)nextBlock : (ushort)0;
            nextBlock += resourceBlocks;

            var entry = new MfsFileDirectoryBlock(
                flags: file.Flags,
                fileType: file.FileType,
                creator: file.Creator,
                finderFlags: file.FinderFlags,
                folderNumber: file.FolderNumber,
                fileNumber: (uint)(i + 1),
                dataForkAllocationBlock: dataStart,
                dataForkSize: (uint)file.DataFork.Length,
                dataForkAllocatedSize: (uint)((long)dataBlocks * allocationBlockSize),
                resourceForkAllocationBlock: resourceStart,
                resourceForkSize: (uint)file.ResourceFork.Length,
                resourceForkAllocatedSize: (uint)((long)resourceBlocks * allocationBlockSize),
                creationDate: timestamp,
                lastModificationDate: timestamp,
                name: new String255(nameBytes.Slice(file.NameOffset, file.NameLength)));

            entry.WriteTo(block[blockOffset..], out _);
            blockOffset += alignedEntrySize;
        }

        // Write the final directory block.
        stream.Write(block);
    }

    private static void WriteAllocationBlocks(Stream stream, ReadOnlySpan<FileDefinition> files, uint allocationBlockSize)
    {
        // Zero padding for the unused tail of each fork's last allocation block.
        ReadOnlySpan<byte> zeros = stackalloc byte[4096];

        foreach (ref readonly FileDefinition file in files)
        {
            WriteForkData(stream, file.DataFork, allocationBlockSize, zeros);
            WriteForkData(stream, file.ResourceFork, allocationBlockSize, zeros);
        }
    }

    private static void WriteForkData(Stream stream, byte[] data, uint allocationBlockSize, ReadOnlySpan<byte> zeros)
    {
        if (data.Length == 0)
        {
            return;
        }

        // The fork occupies whole, consecutive allocation blocks: write it in one call, then
        // zero-fill the rest of its last block.
        stream.Write(data);

        uint remainder = (uint)data.Length % allocationBlockSize;
        int padding = remainder == 0 ? 0 : (int)(allocationBlockSize - remainder);
        while (padding > 0)
        {
            int chunk = Math.Min(zeros.Length, padding);
            stream.Write(zeros[..chunk]);
            padding -= chunk;
        }
    }

    private static int GetAllocationBlockCount(int byteLength, uint allocationBlockSize) =>
        byteLength == 0
            ? 0
            : checked((int)(((ulong)byteLength + allocationBlockSize - 1) / allocationBlockSize));

    internal static void ValidateFileCount(int fileCount)
    {
        if (fileCount < 0 || fileCount > ushort.MaxValue)
        {
            throw new InvalidOperationException($"An MFS volume cannot contain more than {ushort.MaxValue} files.");
        }
    }

    internal static (ushort DirectoryLength, ushort AllocationStart) ValidateDirectoryLayout(
        ushort directoryStart,
        int directoryLength)
    {
        if (directoryLength < 1 || directoryLength > ushort.MaxValue - directoryStart)
        {
            throw new InvalidOperationException("The file directory cannot be addressed within an MFS volume.");
        }

        return ((ushort)directoryLength, (ushort)(directoryStart + directoryLength));
    }

    private static T EncodeString<T>(
        string value,
        Func<ReadOnlySpan<char>, T> encoder,
        string parameterName)
    {
        try
        {
            return encoder(value);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                "Value must not contain nulls and must be representable in Mac OS Roman.",
                parameterName,
                ex);
        }
    }

    // A struct (kept in the list's backing array) so adding a file doesn't allocate an object
    // of its own. The encoded name lives in _nameBytes at NameOffset, NameLength bytes long.
    private readonly record struct FileDefinition(
        int NameOffset,
        byte NameLength,
        String4 FileType,
        String4 Creator,
        byte[] DataFork,
        byte[] ResourceFork,
        MfsFileDirectoryBlockFlags Flags,
        ushort FinderFlags,
        short FolderNumber);
}
