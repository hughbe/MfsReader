namespace MfsReader;

/// <summary>
/// Contains the validated byte ranges that make up an MFS volume.
/// </summary>
internal readonly record struct VolumeLayout(
    long VolumeStart,
    long VolumeEnd,
    long AllocationMapOffset,
    int AllocationMapLength,
    long DirectoryOffset,
    long DirectoryLength,
    long AllocationOffset,
    long AllocationLength,
    int AllocationBlockSize,
    ushort AllocationBlockCount)
{
    internal const int SectorSize = 512;

    public static VolumeLayout Create(
        MfsMasterDirectoryBlock mdb,
        long volumeStart,
        long volumeLength,
        long streamLength)
    {
        try
        {
            checked
            {
                if (volumeStart < 0 || volumeLength < 0 || streamLength < 0)
                {
                    throw new InvalidDataException("Volume bounds must not be negative.");
                }

                long volumeEnd = volumeStart + volumeLength;
                if (volumeEnd > streamLength)
                {
                    throw new InvalidDataException(
                        $"Volume extends beyond stream bounds (end offset {volumeEnd}, stream length {streamLength}).");
                }

                long mdbOffset = volumeStart + MfsVolume.MasterDirectoryBlockOffset;
                EnsureRange(mdbOffset, SectorSize, volumeEnd, "Master directory block");

                if (mdb.AllocationBlockSize > int.MaxValue)
                {
                    throw new InvalidDataException(
                        $"Allocation block size ({mdb.AllocationBlockSize}) exceeds the supported maximum of {int.MaxValue} bytes.");
                }

                int allocationBlockSize = (int)mdb.AllocationBlockSize;
                long allocationMapOffset = mdbOffset + MfsMasterDirectoryBlock.Size;
                int allocationMapLength = (int)(((long)mdb.NumberOfAllocationBlocks * 3 + 1) / 2);
                long directoryOffset = volumeStart + (long)mdb.FileDirectoryStart * SectorSize;
                long directoryLength = (long)mdb.FileDirectoryLength * SectorSize;
                long allocationOffset = volumeStart + (long)mdb.AllocationBlockStart * SectorSize;
                long allocationLength = (long)mdb.NumberOfAllocationBlocks * allocationBlockSize;

                EnsureRange(allocationMapOffset, allocationMapLength, volumeEnd, "Allocation block map");
                EnsureRange(directoryOffset, directoryLength, volumeEnd, "File directory");
                EnsureRange(allocationOffset, allocationLength, volumeEnd, "Allocation block region");

                if (allocationMapOffset + allocationMapLength > directoryOffset)
                {
                    throw new InvalidDataException("Allocation block map overlaps the file directory.");
                }

                if (allocationMapOffset + allocationMapLength > allocationOffset)
                {
                    throw new InvalidDataException("Allocation block map overlaps the allocation block region.");
                }

                // Inside Macintosh: the file directory normally sits between the allocation
                // block map and the allocation blocks, "but it can sit inside the allocation
                // area if its blocks are marked with $FFF's in the block map". Accept either
                // layout, but reject a directory that only partly overlaps the region.
                long directoryEnd = directoryOffset + directoryLength;
                bool directoryBeforeRegion = directoryEnd <= allocationOffset;
                bool directoryInsideRegion = directoryOffset >= allocationOffset
                    && directoryEnd <= allocationOffset + allocationLength;
                if (!directoryBeforeRegion && !directoryInsideRegion)
                {
                    throw new InvalidDataException("File directory overlaps the allocation block region.");
                }

                return new VolumeLayout(
                    volumeStart,
                    volumeEnd,
                    allocationMapOffset,
                    allocationMapLength,
                    directoryOffset,
                    directoryLength,
                    allocationOffset,
                    allocationLength,
                    allocationBlockSize,
                    mdb.NumberOfAllocationBlocks);
            }
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("Volume layout arithmetic overflowed.", ex);
        }
    }

    public bool IsValidBlock(ushort block) => block >= 2 && block < (int)AllocationBlockCount + 2;

    public long GetAllocationBlockOffset(ushort block)
    {
        if (!IsValidBlock(block))
        {
            throw new InvalidDataException(
                $"Allocation block {block} is outside the valid range 2-{AllocationBlockCount + 1}.");
        }

        return checked(AllocationOffset + (long)(block - 2) * AllocationBlockSize);
    }

    private static void EnsureRange(
        long offset,
        long length,
        long volumeEnd,
        string description)
    {
        long end = checked(offset + length);
        // All callers construct offset by adding non-negative checked values to
        // volumeStart, so only the upper bound needs a runtime comparison.
        if (end > volumeEnd)
        {
            throw new InvalidDataException(
                $"{description} extends beyond the volume end ({volumeEnd}).");
        }
    }
}
