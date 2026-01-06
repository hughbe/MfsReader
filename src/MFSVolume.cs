using System.Diagnostics;

namespace MfsReader;

/// <summary>
/// Represents a MFS volume.
/// </summary>
public class MFSVolume
{
    private readonly long _allocationBlockStartOffset;

    /// <summary>
    /// Gets the underlying stream of the MFS volume.
    /// </summary>
    internal Stream Stream { get; }

    /// <summary>
    /// Gets the starting offset of the MFS volume within the stream.
    /// </summary>
    internal long StreamStartOffset { get; }

    /// <summary>
    /// The offset of the master directory block within the MFS volume.
    /// </summary>
    internal const int MasterDirectoryBlockOffset = 1024;

    /// <summary>
    /// Gets the master directory block of the MFS volume.
    /// </summary>
    public MFSMasterDirectoryBlock MasterDirectoryBlock { get; }

    /// <summary>
    /// Gets the allocation block map of the MFS volume.
    /// </summary>
    public MFSAllocationBlockMap AllocationBlockMap { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MFSVolume"/> class and scans for MFS volumes.
    /// </summary>
    /// <param name="stream">The stream containing the disk image data.</param>
    public MFSVolume(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("Stream must be seekable and readable.", nameof(stream));
        }

        Stream = stream;
        StreamStartOffset = (int)stream.Position;

        // The first two blocks are the boot block - they can be skipped
        // for our purposes.
        stream.Seek(StreamStartOffset + MasterDirectoryBlockOffset, SeekOrigin.Begin);

        // The next block is the master directory block.
        // Read the full 512-byte logical block because the allocation block map
        // starts immediately after the 64-byte MDB header in the same block.
        Span<byte> blockBuffer = stackalloc byte[512];
        if (stream.Read(blockBuffer) != blockBuffer.Length)
        {
            throw new InvalidDataException("Unable to read DSK master directory block.");
        }

        MasterDirectoryBlock = new MFSMasterDirectoryBlock(blockBuffer[..MFSMasterDirectoryBlock.Size]);
        AllocationBlockMap = new MFSAllocationBlockMap(blockBuffer, this);

        // From https://www.weihenstephan.org/~michaste/pagetable/mac/Inside_Macintosh.pdf
        // "The first allocation block on a volume typically follows the file
        // directory. It's numbered 2 because of the special meaning of numbers
        // 0 and 1."
        // The allocation blocks start at AllocationBlockStart (in sectors), and allocation
        // block 2 is the first block. So to find allocation block N, we calculate:
        // offset = (AllocationBlockStart * 512) + ((N - 2) * AllocationBlockSize)
        // Which can be rewritten as:
        // offset = (AllocationBlockStart * 512) - (2 * AllocationBlockSize) + (N * AllocationBlockSize)
        // So _allocationBlockStartOffset is the base, and we add (N * AllocationBlockSize)
        _allocationBlockStartOffset = StreamStartOffset + MasterDirectoryBlock.AllocationBlockStart * 512 - 2 * MasterDirectoryBlock.AllocationBlockSize;
    }

    /// <summary>
    /// Gets the entries in the MFS volume.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="InvalidDataException">Thrown if the file directory cannot be read.</exception>
    public IEnumerable<MFSFileDirectoryBlock> GetEntries()
    {
        var blockBuffer = new byte[512];

        // From https://www.weihenstephan.org/~michaste/pagetable/mac/Inside_Macintosh.pdf
        // "A file directory entry contains 51 bytes plus one byte for each
        // character in the file name."
        // "Entries are always an integral number of words and don't cross
        // logical block boundaries"

        // Seek to the file directory start.
        Stream.Seek(StreamStartOffset + MasterDirectoryBlock.FileDirectoryStart * 512, SeekOrigin.Begin);
        for (int block = 0; block < MasterDirectoryBlock.FileDirectoryLength; block++)
        {
            // Read the next block.
            if (Stream.Read(blockBuffer, 0, blockBuffer.Length) != blockBuffer.Length)
            {
                throw new InvalidDataException("Unable to read DSK file directory block.");
            }

            int offset = 0;
            while (offset < blockBuffer.Length)
            {
                // Check for end of directory entries in this block.
                var blockFlags = (MFSFileDirectoryBlockFlags)blockBuffer[offset];
                if (!blockFlags.HasFlag(MFSFileDirectoryBlockFlags.EntryUsed))
                {
                    break;
                }
                
                var fileEntry = new MFSFileDirectoryBlock(blockBuffer.AsSpan()[offset..], out var bytesRead);
                offset += bytesRead;

                // Align to next word boundary.
                if ((offset & 1) != 0)
                {
                    offset++;
                }

                yield return fileEntry;
            }
        }
    }

    /// <summary>
    /// Gets the data fork data of a file as a byte array.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <returns>>The data fork data as a byte array.</returns>
    public byte[] GetDataForkData(MFSFileDirectoryBlock file)
    {
        return GetFileData(file, MFSForkType.DataFork);
    }

    /// <summary>
    /// Gets the data fork data of a file and writes it to the specified output stream.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <param name="outputStream">The stream to write the data fork data to.</param>
    /// <returns> The number of bytes written to the output stream.</returns>
    public int GetDataForkData(MFSFileDirectoryBlock file, Stream outputStream)
    {
        return GetFileData(file, outputStream, MFSForkType.DataFork);
    }

    /// <summary>
    /// Gets the resource fork data of a file as a byte array.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <returns>The resource fork data as a byte array.</returns>
    public byte[] GetResourceForkData(MFSFileDirectoryBlock file)
    {
        return GetFileData(file, MFSForkType.ResourceFork);
    }

    /// <summary>
    /// Gets the resource fork data of a file and writes it to the specified output stream.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <param name="outputStream">The stream to write the resource fork data to.</param>
    /// <returns> The number of bytes written to the output stream.</returns>
    public int GetResourceForkData(MFSFileDirectoryBlock file, Stream outputStream)
    {
        return GetFileData(file, outputStream, MFSForkType.ResourceFork);
    }

    /// <summary>
    /// Gets the data of a file as a byte array.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <param name="forkType">The type of fork to read (data or resource).</param>
    /// <returns>The file data as a byte array.</returns>
    public byte[] GetFileData(MFSFileDirectoryBlock file, MFSForkType forkType)
    {
        using var ms = new MemoryStream();
        GetFileData(file, ms, forkType);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes the data of a file to the specified output stream.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <param name="outputStream">The stream to write the file data to.</param>
    /// <param name="forkType">The type of fork to read (data or resource).</param>
    /// <returns>The number of bytes written to the output stream.</returns>
    public int GetFileData(MFSFileDirectoryBlock file, Stream outputStream, MFSForkType forkType)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        if (!Enum.IsDefined(forkType))
        {
            throw new ArgumentOutOfRangeException(nameof(forkType), "Invalid fork type specified.");
        }

        if (forkType == MFSForkType.DataFork)
        {
            return ReadBlock(file.DataForkAllocationBlock, file.DataForkSize, outputStream);
        }
        else
        {
            return ReadBlock(file.ResourceForkAllocationBlock, file.ResourceForkSize, outputStream);
        }
    }

    /// <summary>
    /// Reads data from an allocation block and writes it to the output stream.
    /// </summary>
    /// <param name="allocationBlock">The starting allocation block number.</param>
    /// <param name="size">The number of bytes to read.</param>
    /// <param name="outputStream">The stream to write the data to.</param>
    /// <returns>The number of bytes written to the output stream.</returns>
    private int ReadBlock(ushort allocationBlock, uint size, Stream outputStream)
    {
        if (allocationBlock == 0 || size == 0)
        {
            return 0;
        }

        // Read and write the data in chunks
        Span<byte> buffer = stackalloc byte[(int)MasterDirectoryBlock.AllocationBlockSize];
        int totalBytesRead = 0;
        uint remainingBytes = size;

        while (remainingBytes > 0)
        {
            // Calculate the byte offset of the allocation block.
            // Allocation blocks start after the boot blocks (2 blocks), master directory block (1 block),
            // and the file directory blocks.
            long offset = _allocationBlockStartOffset + (allocationBlock * (long)MasterDirectoryBlock.AllocationBlockSize);

            // Validate that the offset is within the stream bounds
            if (offset < 0 || offset >= Stream.Length)
            {
                throw new InvalidDataException($"Allocation block {allocationBlock} points to an offset ({offset}) outside the valid stream range (0-{Stream.Length}).");
            }

            Stream.Seek(offset, SeekOrigin.Begin);

            // Read the allocation block data.            
            int bytesToRead = (int)Math.Min(buffer.Length, remainingBytes);
            if (Stream.Read(buffer[..bytesToRead]) != bytesToRead)
            {
                throw new InvalidDataException($"Unexpected end of stream while reading allocation block {allocationBlock}.");
            }

            outputStream.Write(buffer[..bytesToRead]);
            totalBytesRead += bytesToRead;
            remainingBytes -= (uint)bytesToRead;

            ushort nextBlock = AllocationBlockMap.GetNextAllocationBlock(allocationBlock);
            if (nextBlock == 1 || nextBlock == 0)
            {
                // End of file (1) or unused (0) - we're done
                break;
            }

            allocationBlock = nextBlock;
        }

        return totalBytesRead;
    }
}
