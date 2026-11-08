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

    // The volume shares one stream, and every read is a seek followed by a read. The lock
    // makes those pairs atomic so the volume can be read from several threads at once.
    private readonly Lock _streamLock = new();

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
    /// keeps using the stream, so the caller must keep it open and must not use it
    /// elsewhere while the volume is in use. Reading from the volume is thread safe.
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
        ReadAt(
            _layout.DirectoryOffset + (long)sectorIndex * VolumeLayout.SectorSize,
            destination[..VolumeLayout.SectorSize]);
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
    /// <exception cref="InvalidDataException">
    /// Thrown if the fork is larger than the volume can hold, or its allocation block chain is
    /// invalid.
    /// </exception>
    public byte[] GetFileData(MfsFileDirectoryBlock file, MfsForkType forkType)
    {
        (ushort firstBlock, uint size) = GetFork(file, forkType);
        if (size == 0)
        {
            return [];
        }

        // Checked before allocating: the size comes from the (untrusted) directory entry, and
        // a corrupt entry must not be able to request an array larger than the volume itself.
        ValidateForkSize(size);

        var result = new byte[(int)size];
        Span<ulong> visited = stackalloc ulong[VisitedWordCount];
        var chain = new ChainReader(this, firstBlock, size, visited);

        // Read each contiguous run of allocation blocks straight into the result.
        int position = 0;
        while (chain.TryGetNextRun(int.MaxValue, out long offset, out int length))
        {
            ReadAt(offset, result.AsSpan(position, length));
            position += length;
        }

        return result;
    }

    /// <summary>
    /// Writes the data of a file to the specified output stream.
    /// </summary>
    /// <param name="file">The file to read.</param>
    /// <param name="outputStream">The stream to write the file data to.</param>
    /// <param name="forkType">The type of fork to read (data or resource).</param>
    /// <returns>The number of bytes written to the output stream.</returns>
    /// <remarks>
    /// If the allocation block chain turns out to be damaged, the data from the blocks before
    /// the damage has already been written to <paramref name="outputStream"/> when the
    /// <see cref="InvalidDataException"/> is thrown, which allows partial recovery.
    /// </remarks>
    public int GetFileData(MfsFileDirectoryBlock file, Stream outputStream, MfsForkType forkType)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        if (!outputStream.CanWrite)
        {
            throw new ArgumentException("Output stream must be writable.", nameof(outputStream));
        }

        (ushort firstBlock, uint size) = GetFork(file, forkType);
        if (size == 0)
        {
            return 0;
        }

        ValidateForkSize(size);

        // A bounded transfer buffer, so a large allocation block or fork does not require
        // renting an equally large array.
        int bufferSize = (int)Math.Min(size, (uint)MaxTransferBufferSize);
        byte[]? rentedBuffer = null;
        Span<byte> buffer = bufferSize <= 4096
            ? stackalloc byte[bufferSize]
            : (rentedBuffer = ArrayPool<byte>.Shared.Rent(bufferSize)).AsSpan(0, bufferSize);

        try
        {
            Span<ulong> visited = stackalloc ulong[VisitedWordCount];
            var chain = new ChainReader(this, firstBlock, size, visited);

            int totalBytesRead = 0;
            while (chain.TryGetNextRun(bufferSize, out long offset, out int length))
            {
                while (length > 0)
                {
                    int bytesToRead = Math.Min(buffer.Length, length);
                    ReadAt(offset, buffer[..bytesToRead]);
                    outputStream.Write(buffer[..bytesToRead]);
                    offset += bytesToRead;
                    length -= bytesToRead;
                    totalBytesRead += bytesToRead;
                }
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

    // One bit per allocation block, used to detect chains that revisit a block.
    private int VisitedWordCount => (MasterDirectoryBlock.NumberOfAllocationBlocks + 63) / 64;

    // The seek and the read must not be separated by another thread's seek.
    private void ReadAt(long offset, Span<byte> destination)
    {
        lock (_streamLock)
        {
            Stream.Seek(offset, SeekOrigin.Begin);
            Stream.ReadExactly(destination);
        }
    }

    /// <summary>
    /// Walks an allocation block chain, returning it as runs of physically contiguous blocks
    /// so each run can be read with a single seek and read.
    /// </summary>
    private ref struct ChainReader
    {
        private readonly MfsVolume _volume;
        private readonly Span<ulong> _visited;
        private ushort _block;
        private uint _remaining;
        private string? _deferredError;

        public ChainReader(MfsVolume volume, ushort firstBlock, uint size, Span<ulong> visited)
        {
            _volume = volume;
            _block = firstBlock;
            _remaining = size;
            _visited = visited;
            _deferredError = null;
        }

        /// <summary>
        /// Gets the next run of contiguous blocks.
        /// </summary>
        /// <param name="maxRunBytes">
        /// A soft limit: a run stops growing once adding another whole block would exceed it.
        /// A single block is returned whole even if it is larger than this.
        /// </param>
        /// <param name="offset">The stream offset of the run.</param>
        /// <param name="length">The number of fork bytes in the run.</param>
        /// <returns>False once the whole fork has been returned.</returns>
        public bool TryGetNextRun(int maxRunBytes, out long offset, out int length)
        {
            // A damaged chain is reported on the call after the last good run, so the caller
            // receives all the data that precedes the damage first.
            if (_deferredError != null)
            {
                throw new InvalidDataException(_deferredError);
            }

            if (_remaining == 0)
            {
                offset = 0;
                length = 0;
                return false;
            }

            ref readonly VolumeLayout layout = ref _volume._layout;
            int blockSize = layout.AllocationBlockSize;

            offset = layout.GetAllocationBlockOffset(_block);
            if (IsVisited(_block))
            {
                throw new InvalidDataException("Allocation block chain contains a cycle.");
            }

            MarkVisited(_block);

            long runBytes = Math.Min((long)blockSize, _remaining);
            uint left = _remaining - (uint)runBytes;
            ushort last = _block;

            // Grow the run while the chain continues with the physically next block.
            while (left > 0 && runBytes + blockSize <= maxRunBytes)
            {
                ushort next = _volume.AllocationBlockMap.GetNextAllocationBlock(last);
                if (next != last + 1 || !layout.IsValidBlock(next) || IsVisited(next))
                {
                    // Not contiguous, or a problem that the next call reports precisely.
                    break;
                }

                MarkVisited(next);
                last = next;
                uint take = Math.Min((uint)blockSize, left);
                runBytes += take;
                left -= take;
            }

            length = (int)runBytes;
            _remaining = left;

            if (left > 0)
            {
                ushort next = _volume.AllocationBlockMap.GetNextAllocationBlock(last);
                if (next == 0 || next == 1)
                {
                    _deferredError =
                        $"Allocation block chain ended before the declared fork size was read ({left} bytes missing).";
                }
                else
                {
                    _block = next;
                }
            }

            return true;
        }

        private readonly bool IsVisited(ushort block)
        {
            int index = block - 2;
            return (_visited[index >> 6] & (1UL << (index & 63))) != 0;
        }

        private readonly void MarkVisited(ushort block)
        {
            int index = block - 2;
            _visited[index >> 6] |= 1UL << (index & 63);
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

    private void ValidateForkSize(uint size)
    {
        if (size > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Fork size ({size}) exceeds the maximum supported size of {int.MaxValue} bytes.");
        }

        // A chain can't visit any block twice, so no fork can be larger than the whole
        // allocation area. Failing here, before any buffer is allocated, stops a corrupt
        // directory entry from forcing a huge allocation on a tiny image.
        if (size > _layout.AllocationLength)
        {
            throw new InvalidDataException(
                $"Fork size ({size}) exceeds the capacity of the volume ({_layout.AllocationLength} bytes).");
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
