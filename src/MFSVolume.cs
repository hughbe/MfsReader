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
        Span<byte> blockBuffer = stackalloc byte[512];
        if (stream.Read(blockBuffer) != blockBuffer.Length)
        {
            throw new InvalidDataException("Unable to read DSK master directory block.");
        }

        MasterDirectoryBlock = new MFSMasterDirectoryBlock(blockBuffer);
        AllocationBlockMap = new MFSAllocationBlockMap(blockBuffer, this);
        

        // From https://www.weihenstephan.org/~michaste/pagetable/mac/Inside_Macintosh.pdf
        // "The first allocation block on a volume typically follows the file
        // directory. It's numbered 2 because of the special meaning of numbers
        // 0 and 1."
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
                if ((blockBuffer[offset] & (byte)MFSFileDirectoryBlockFlags.EntryUsed) == 0)
                {
                    break;
                }
                
                var fileEntry = new MFSFileDirectoryBlock(blockBuffer[offset..]);
                offset += 51 + fileEntry.Name.Length;

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
    /// Gets the data of a file as a byte array.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <param name="resourceFork">True to read the resource fork; otherwise, false for the data fork.</param>
    /// <returns>The file data as a byte array.</returns>
    public byte[] GetFileData(MFSFileDirectoryBlock file, bool resourceFork)
    {
        using var ms = new MemoryStream();
        GetFileData(file, ms, resourceFork);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes the data of a file to the specified output stream.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <param name="outputStream">The stream to write the file data to.</param>
    /// <param name="resourceFork">True to read the resource fork; otherwise, false for the data fork.</param>
    /// <returns>The number of bytes written to the output stream.</returns>
    public int GetFileData(MFSFileDirectoryBlock file, Stream outputStream, bool resourceFork)
    {
        ArgumentNullException.ThrowIfNull(outputStream);

        if (!resourceFork)
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

            allocationBlock = AllocationBlockMap.GetNextAllocationBlock(allocationBlock);
        }

        return totalBytesRead;
    }
}
