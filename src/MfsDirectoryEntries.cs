using System.Collections;
using System.Runtime.CompilerServices;

namespace MfsReader;

/// <summary>
/// The file directory entries of an <see cref="MfsVolume"/>. Use <c>foreach</c> to enumerate
/// without allocating, or any LINQ method via <see cref="IEnumerable{T}"/>.
/// </summary>
/// <remarks>
/// Nothing is read until enumeration starts, and each enumeration reads the directory afresh.
/// </remarks>
public readonly struct MfsDirectoryEntries : IEnumerable<MfsFileDirectoryBlock>
{
    private readonly MfsVolume? _volume;

    internal MfsDirectoryEntries(MfsVolume volume)
    {
        _volume = volume;
    }

    /// <summary>
    /// Gets an enumerator over the entries.
    /// </summary>
    /// <returns>A struct enumerator that does not allocate.</returns>
    /// <exception cref="InvalidOperationException">Thrown for a default (uninitialized) value.</exception>
    public Enumerator GetEnumerator() =>
        new(_volume ?? throw new InvalidOperationException("The entries were not obtained from a volume."));

    /// <inheritdoc/>
    IEnumerator<MfsFileDirectoryBlock> IEnumerable<MfsFileDirectoryBlock>.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Enumerates the entries of the file directory one sector at a time.
    /// </summary>
    public struct Enumerator : IEnumerator<MfsFileDirectoryBlock>
    {
        // The sector buffer is part of the enumerator itself rather than rented from a pool.
        [InlineArray(VolumeLayout.SectorSize)]
        private struct SectorBuffer
        {
            private byte _element0;
        }

        private readonly MfsVolume _volume;
        private readonly int _sectorCount;
        private SectorBuffer _sector;
        private int _nextSector;
        private int _offset;
        private MfsFileDirectoryBlock _current;

        internal Enumerator(MfsVolume volume)
        {
            _volume = volume;
            _sectorCount = volume.DirectorySectorCount;
            _offset = VolumeLayout.SectorSize; // forces the first sector to be read
        }

        /// <inheritdoc/>
        public readonly MfsFileDirectoryBlock Current => _current;

        /// <inheritdoc/>
        readonly object IEnumerator.Current => _current;

        /// <inheritdoc/>
        public bool MoveNext()
        {
            while (true)
            {
                if (TryReadEntryFromSector())
                {
                    return true;
                }

                if (_nextSector >= _sectorCount)
                {
                    _current = default;
                    return false;
                }

                _volume.ReadDirectorySector(_nextSector++, _sector);
                _offset = 0;
            }
        }

        /// <inheritdoc/>
        public void Reset()
        {
            _nextSector = 0;
            _offset = VolumeLayout.SectorSize;
            _current = default;
        }

        /// <inheritdoc/>
        public readonly void Dispose()
        {
        }

        private bool TryReadEntryFromSector()
        {
            ReadOnlySpan<byte> sector = _sector;
            while (_offset + MfsFileDirectoryBlock.MinSize <= VolumeLayout.SectorSize)
            {
                var flags = (MfsFileDirectoryBlockFlags)sector[_offset];
                if (flags == 0)
                {
                    // Like the Macintosh File Manager, treat a zero flags byte as the end
                    // of the entries in this sector. The ROM tests the whole byte
                    // ("TST.B FlFlags ; flags=0 means end of entries this blk"), and its
                    // delete routine closes the gap by moving later entries down and zeroing
                    // the rest of the sector, so no used entry follows a zero byte.
                    break;
                }

                if ((flags & MfsFileDirectoryBlockFlags.EntryUsed) == 0)
                {
                    // A non-zero flags byte without the used bit. The ROM still walks over
                    // it as an occupied slot (using the entry length), but Inside Macintosh
                    // defines bit 7 as "entry used", so don't report it as a file.
                    int unusedSize = MfsFileDirectoryBlock.MinSize
                        + sector[_offset + MfsFileDirectoryBlock.MinSize - 1];
                    unusedSize += unusedSize & 1;
                    if (_offset + unusedSize > VolumeLayout.SectorSize)
                    {
                        break;
                    }

                    _offset += unusedSize;
                    continue;
                }

                _current = new MfsFileDirectoryBlock(sector[_offset..], out int bytesRead);

                // Entries are word aligned.
                _offset += (bytesRead + 1) & ~1;
                return true;
            }

            _offset = VolumeLayout.SectorSize;
            return false;
        }
    }
}
