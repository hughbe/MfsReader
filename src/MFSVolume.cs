using System.Buffers;
using System.Diagnostics;

namespace MfsReader;

/// <summary>
/// Represents a MFS volume.
/// </summary>
public class MfsVolume
{
    private const int MaxTransferBufferSize = 64 * 1024;
    private readonly VolumeLayout _layout;

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
    public MfsMasterDirectoryBlock MasterDirectoryBlock { get; }

    /// <summary>
    /// Gets the allocation block map of the MFS volume.
    /// </summary>
    public MfsAllocationBlockMap AllocationBlockMap { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MfsVolume"/> class.
    /// </summary>
    /// <param name="stream">
    /// The stream containing the volume. The volume is taken to start at the stream's
    /// <em>current position</em> and to extend to the end of the stream, so a stream
    /// holding a bare disk image must be positioned at 0 (the default for a newly opened
    /// stream). The stream position after construction is unspecified, and the volume
    /// keeps using the stream, so the caller must keep it open and must not share it
    /// between threads.
    /// </param>
    /// <exception cref="InvalidDataException">Thrown if the stream does not contain a valid MFS volume.</exception>
    public MfsVolume(Stream stream)
        : this(stream, GetRemainingLength(stream))
    {
    }

    /// <summary>
    /// Initializes a volume whose data is restricted to a region of the stream.
    /// </summary>
    internal MfsVolume(Stream stream, long volumeLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("Stream must be seekable and readable.", nameof(stream));
        }

        Stream = stream;
        StreamStartOffset = stream.Position;

        ValidateVolumeBounds(StreamStartOffset, volumeLength, stream.Length);

        // The first two blocks are the boot block - they can be skipped
        // for our purposes.
        stream.Seek(StreamStartOffset + MasterDirectoryBlockOffset, SeekOrigin.Begin);

        // The next block is the master directory block.
        // Read the full 512-byte logical block because the allocation block map
        // starts immediately after the 64-byte MDB header in the same block.
        Span<byte> blockBuffer = stackalloc byte[512];
        stream.ReadExactly(blockBuffer);

        MasterDirectoryBlock = new MfsMasterDirectoryBlock(blockBuffer[..MfsMasterDirectoryBlock.Size]);
        _layout = VolumeLayout.Create(MasterDirectoryBlock, StreamStartOffset, volumeLength, stream.Length);
        AllocationBlockMap = new MfsAllocationBlockMap(blockBuffer, this);
    }

    /// <summary>
    /// Gets the entries in the MFS volume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enumeration follows the Macintosh File Manager. Within each directory sector, a zero
    /// flags byte ends the entries (the ROM keeps entries packed at the start of the sector
    /// and zeroes the rest when a file is deleted). The number of files declared by the
    /// master directory block is only a hint: the ROM corrects it when it disagrees with
    /// the directory, so it is not enforced here. Entries with a non-zero flags byte that
    /// lacks the "entry used" bit are skipped rather than reported.
    /// </para>
    /// <para>
    /// The returned value is a struct whose <c>foreach</c> enumerator also lives on the
    /// stack, so enumerating with <c>foreach</c> does not allocate. It also implements
    /// <see cref="IEnumerable{T}"/>, so LINQ and <c>ToList()</c> work as usual (boxing it once).
    /// </para>
    /// </remarks>
    /// <returns>The used entries in the file directory.</returns>
    /// <exception cref="InvalidDataException">Thrown if a file directory entry is malformed.</exception>
    public MfsDirectoryEntries GetEntries() => new(this);

    /// <summary>
    /// The number of 512-byte sectors in the file directory.
    /// </summary>
    internal int DirectorySectorCount => (int)(_layout.DirectoryLength / VolumeLayout.SectorSize);

    /// <summary>
    /// Reads one sector of the file directory.
    /// </summary>
    /// <remarks>
    /// Seeks every time because a caller may read a fork while lazily enumerating entries,
    /// which also moves the shared stream.
    /// </remarks>
    internal void ReadDirectorySector(int sectorIndex, Span<byte> destination)
    {
        Stream.Seek(_layout.DirectoryOffset + (long)sectorIndex * VolumeLayout.SectorSize, SeekOrigin.Begin);
        Stream.ReadExactly(destination[..VolumeLayout.SectorSize]);
    }

    /// <summary>
    /// Gets the data fork data of a file as a byte array.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <returns>>The data fork data as a byte array.</returns>
    public byte[] GetDataForkData(MfsFileDirectoryBlock file)
        => GetFileData(file, MfsForkType.DataFork);

    /// <summary>
    /// Gets the data fork data of a file and writes it to the specified output stream.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <param name="outputStream">The stream to write the data fork data to.</param>
    /// <returns> The number of bytes written to the output stream.</returns>
    public int GetDataForkData(MfsFileDirectoryBlock file, Stream outputStream)
        => GetFileData(file, outputStream, MfsForkType.DataFork);

    /// <summary>
    /// Gets the resource fork data of a file as a byte array.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <returns>The resource fork data as a byte array.</returns>
    public byte[] GetResourceForkData(MfsFileDirectoryBlock file)
        => GetFileData(file, MfsForkType.ResourceFork);

    /// <summary>
    /// Gets the resource fork data of a file and writes it to the specified output stream.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <param name="outputStream">The stream to write the resource fork data to.</param>
    /// <returns> The number of bytes written to the output stream.</returns>
    public int GetResourceForkData(MfsFileDirectoryBlock file, Stream outputStream)
        => GetFileData(file, outputStream, MfsForkType.ResourceFork);

    /// <summary>
    /// Gets the data of a file as a byte array.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <param name="forkType">The type of fork to read (data or resource).</param>
    /// <returns>The file data as a byte array.</returns>
    public byte[] GetFileData(MfsFileDirectoryBlock file, MfsForkType forkType)
    {
        (ushort allocationBlock, uint size) = GetFork(file, forkType);
        if (size == 0)
        {
            return [];
        }

        ValidateForkSize(size);
        var result = new byte[(int)size];
        using var ms = new MemoryStream(result, writable: true);
        ReadBlock(allocationBlock, size, ms);
        return result;
    }

    /// <summary>
    /// Writes the data of a file to the specified output stream.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <param name="outputStream">The stream to write the file data to.</param>
    /// <param name="forkType">The type of fork to read (data or resource).</param>
    /// <returns>The number of bytes written to the output stream.</returns>
    public int GetFileData(MfsFileDirectoryBlock file, Stream outputStream, MfsForkType forkType)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        if (!outputStream.CanWrite)
        {
            throw new ArgumentException("Output stream must be writable.", nameof(outputStream));
        }

        (ushort allocationBlock, uint size) = GetFork(file, forkType);
        return ReadBlock(allocationBlock, size, outputStream);
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

        ValidateForkSize(size);

        // Use a bounded transfer buffer so a large allocation block does not require
        // renting an equally large array.
        int blockSize = _layout.AllocationBlockSize;
        int bufferSize = Math.Min(blockSize, MaxTransferBufferSize);
        byte[]? rentedBuffer = null;
        Span<byte> buffer = bufferSize <= 4096
            ? stackalloc byte[bufferSize]
            : (rentedBuffer = ArrayPool<byte>.Shared.Rent(bufferSize)).AsSpan(0, bufferSize);

        try
        {
            int totalBytesRead = 0;
            uint remainingBytes = size;
            int maxBlocks = MasterDirectoryBlock.NumberOfAllocationBlocks;
            Span<ulong> visitedBlocks = stackalloc ulong[(maxBlocks + 63) / 64];

            while (remainingBytes > 0)
            {
                long offset = _layout.GetAllocationBlockOffset(allocationBlock);

                int blockIndex = allocationBlock - 2;
                int visitedWord = blockIndex / 64;
                ulong visitedMask = 1UL << (blockIndex % 64);
                if ((visitedBlocks[visitedWord] & visitedMask) != 0)
                {
                    throw new InvalidDataException("Allocation block chain contains a cycle.");
                }

                visitedBlocks[visitedWord] |= visitedMask;

                Stream.Seek(offset, SeekOrigin.Begin);

                int bytesInBlock = (int)Math.Min((uint)blockSize, remainingBytes);
                int bytesLeftInBlock = bytesInBlock;
                while (bytesLeftInBlock > 0)
                {
                    int bytesToRead = Math.Min(buffer.Length, bytesLeftInBlock);
                    Stream.ReadExactly(buffer[..bytesToRead]);
                    outputStream.Write(buffer[..bytesToRead]);
                    totalBytesRead = checked(totalBytesRead + bytesToRead);
                    remainingBytes -= (uint)bytesToRead;
                    bytesLeftInBlock -= bytesToRead;
                }

                if (remainingBytes == 0)
                {
                    break;
                }

                ushort nextBlock = AllocationBlockMap.GetNextAllocationBlock(allocationBlock);
                if (nextBlock == 1 || nextBlock == 0)
                {
                    throw new InvalidDataException(
                        $"Allocation block chain ended before the declared fork size was read ({remainingBytes} bytes missing).");
                }

                allocationBlock = nextBlock;
            }

            return totalBytesRead;
        }
        finally
        {
            if (rentedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
    }

    private static long GetRemainingLength(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("Stream must be seekable and readable.", nameof(stream));
        }

        return checked(stream.Length - stream.Position);
    }

    private static (ushort AllocationBlock, uint Size) GetFork(
        MfsFileDirectoryBlock file,
        MfsForkType forkType) =>
        forkType switch
        {
            MfsForkType.DataFork => (file.DataForkAllocationBlock, file.DataForkSize),
            MfsForkType.ResourceFork => (file.ResourceForkAllocationBlock, file.ResourceForkSize),
            _ => throw new ArgumentOutOfRangeException(nameof(forkType), "Invalid fork type specified."),
        };

    private static void ValidateForkSize(uint size)
    {
        if (size > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Fork size ({size}) exceeds the maximum supported size of {int.MaxValue} bytes.");
        }
    }

    private static void ValidateVolumeBounds(long volumeStart, long volumeLength, long streamLength)
    {
        try
        {
            checked
            {
                if (volumeStart < 0 || volumeLength < 0 || volumeStart + volumeLength > streamLength)
                {
                    throw new InvalidDataException("Volume lies outside the stream bounds.");
                }

                long volumeEnd = volumeStart + volumeLength;
                long mdbSectorEnd = volumeStart + MasterDirectoryBlockOffset + VolumeLayout.SectorSize;
                if (mdbSectorEnd > volumeEnd)
                {
                    throw new InvalidDataException("Volume is too small to contain a master directory block.");
                }
            }
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("Volume bounds arithmetic overflowed.", ex);
        }
    }
}
