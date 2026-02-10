using System.Buffers.Binary;
using System.Diagnostics;
using MfsReader.Utilities;

namespace MfsReader;

/// <summary>
/// Represents the master directory block of an MFS volume.
/// </summary>
public struct MfsMasterDirectoryBlock
{
    /// <summary>
    /// The size of the master directory block, in bytes.
    /// </summary>
    public const int Size = 64;

    /// <summary>
    /// Gets the volume signature.
    /// </summary>
    public ushort Signature { get; }

    /// <summary>
    /// Gets the creation date of the volume.
    /// </summary>
    public MfsTimestamp CreationDate { get; }

    /// <summary>
    /// Gets the last backup date of the volume.
    /// </summary>
    public MfsTimestamp LastBackupDate { get; }

    /// <summary>
    /// Gets the attributes of the master directory block.
    /// </summary>
    public MfsMasterDirectoryBlockAttributes Attributes { get; }

    /// <summary>
    /// Gets the number of files in the volume.
    /// </summary>
    public ushort NumberOfFiles { get; }

    /// <summary>
    /// Gets the starting sector of the file directory, relative to
    /// the start of the volume.
    /// </summary>
    public ushort FileDirectoryStart { get; }

    /// <summary>
    /// Gets the length of the file directory, in sectors.
    /// </summary>
    public ushort FileDirectoryLength { get; }

    /// <summary>
    /// Gets the number of allocation blocks on the volume.
    /// </summary>
    public ushort NumberOfAllocationBlocks { get; }

    /// <summary>
    /// Gets the size of allocation blocks, in bytes.
    /// </summary>
    public uint AllocationBlockSize { get; }

    /// <summary>
    /// Gets the clump size, in bytes.
    /// </summary>
    public uint ClumpSize { get; }

    /// <summary>
    /// Gets the starting sector of the first allocation block,
    /// relative to the start of the volume.
    /// </summary>
    public ushort AllocationBlockStart { get; }

    /// <summary>
    /// Gets the next file number to be assigned.
    /// </summary>
    public uint NextFileNumber { get; }

    /// <summary>
    /// Gets the number of free allocation blocks on the volume.
    /// </summary>
    public ushort FreeAllocationBlocks { get; }

    /// <summary>
    /// Gets the volume name.
    /// </summary>
    public String27 VolumeName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MfsMasterDirectoryBlock"/> struct from the given data.
    /// </summary>
    /// <param name="data">The span containing the master directory block data.</param>
    /// <exception cref="ArgumentException">Thrown when the provided data is not the correct size.</exception>
    public MfsMasterDirectoryBlock(ReadOnlySpan<byte> data)
    {
        if (data.Length != Size)
        {
            throw new ArgumentException($"Master Directory Block data must be exactly {Size} bytes long.", nameof(data));
        }

        int offset = 0;

        // drSigWord (word)
        // Signature. This must always be the value 0xD2D7 to identify a MFS volume.
        Signature = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;
        if (Signature != 0xD2D7) // MFS signature
        {
            if (Signature == 0x4244) // 'BD' - HFS signature
            {
                throw new InvalidDataException("The provided data appears to be an HFS volume, not MFS.");
            }

            throw new InvalidDataException($"Invalid MFS master directory block signature.");
        }

        // drCrDate (long word) date and time of initialization
        // Creation date, in seconds since midnight January 1st 1904.
        CreationDate = new MfsTimestamp(data.Slice(offset, MfsTimestamp.Size));
        offset += MfsTimestamp.Size;

        // drLsBkUp (long word) date and time of last backup
        // Last backup date, in seconds since midnight January 1st 1904.
        LastBackupDate = new MfsTimestamp(data.Slice(offset, MfsTimestamp.Size));
        offset += MfsTimestamp.Size;

        // drAtrb (word) volume attributes 
        // Attributes. Bit 7 is set if the volume is locked by hardware.
        // Bit 15 is set if the volume is locked by software. Other bits
        // are unknown.
        Attributes = (MfsMasterDirectoryBlockAttributes)BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;

        // drNmFls (word) number of files in directory
        // Number of files.
        NumberOfFiles = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;

        // drDirSt (word) first block of directory
        // File directory start. This is the first sector of the directory,
        // relative to the start of the volume.
        FileDirectoryStart = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;

        // drBILen (word) length of directory in blocks 
        // File directory length. This is the length of the directory, in sectors.
        FileDirectoryLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;

        // drNmAIBIks (word) number of allocation blocks on volume
        // Number of allocation blocks.
        NumberOfAllocationBlocks = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;

        // The allocation block map uses 12-bit entries, so block numbers range from 2 to 4095.
        // This limits the maximum number of allocation blocks to 4094.
        if (NumberOfAllocationBlocks > 4094)
        {
            throw new InvalidDataException($"Number of allocation blocks ({NumberOfAllocationBlocks}) exceeds the 12-bit allocation block map limit of 4094.");
        }

        // drAlBlkSiz (long word) size of allocation blocks
        // Allocation block size, in bytes. This may be any multiple of 512, not just powers of 2.
        AllocationBlockSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        offset += 4;

        if (AllocationBlockSize == 0 || (AllocationBlockSize % 512) != 0)
        {
            throw new ArgumentException("Invalid allocation block size in MFS master directory block.", nameof(data));
        }

        // drClpSiz (long word) number of bytes to allocate
        // Clump size, in bytes. This is a hint to the filesystem driver for how many allocation blocks to reserve when increasing the size of a file. This must be a multiple of the allocation block size.
        ClumpSize = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;

        if (ClumpSize != 0 && (ClumpSize % AllocationBlockSize) != 0)
        {
            throw new InvalidDataException($"Clump size ({ClumpSize}) must be a multiple of the allocation block size ({AllocationBlockSize}).");
        }

        // drAIBISt (word) first allocation block in block map
        // Allocation block start. This is the first sector of the first allocation block, relative to the start of the volume.
        AllocationBlockStart = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;

        // drNxtFNum (long word)
        // Next file number. This is a hint to the filesystem driver for which file number it should assign to the next file.
        NextFileNumber = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;
        
        // drFreeBks (word) number of unused allocation blocks
        // Free allocation blocks.
        FreeAllocationBlocks = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;

        if (FreeAllocationBlocks > NumberOfAllocationBlocks)
        {
            throw new InvalidDataException($"Free allocation blocks ({FreeAllocationBlocks}) exceeds total number of allocation blocks ({NumberOfAllocationBlocks}).");
        }

        // drVN (byte) length of volume name.
        var volumeNameLength = data[offset];
        offset += 1;

        if (volumeNameLength > String27.Size)
        {
            throw new ArgumentException($"Volume name length {volumeNameLength} exceeds maximum of {String27.Size} bytes.", nameof(data));
        }

        // drVN + 1 (bytes) characters of volume name
        // Volume name. This field has a fixed length, but it contains a
        // variable-length Pascal string. The first byte indicates the
        // number of characters in the string, and that many of the following
        // bytes contain the string. Any remaining bytes should be padded with
        // zeroes.
        VolumeName = new String27(data.Slice(offset, volumeNameLength));
        offset += String27.Size;

        Debug.Assert(offset == data.Length, "Did not read all Master Directory Block data.");
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MfsMasterDirectoryBlock"/> struct with the specified values.
    /// </summary>
    /// <param name="creationDate">The creation date of the volume.</param>
    /// <param name="attributes">The volume attributes.</param>
    /// <param name="numberOfFiles">The number of files in the volume.</param>
    /// <param name="fileDirectoryStart">The starting sector of the file directory.</param>
    /// <param name="fileDirectoryLength">The length of the file directory, in sectors.</param>
    /// <param name="numberOfAllocationBlocks">The number of allocation blocks on the volume.</param>
    /// <param name="allocationBlockSize">The size of allocation blocks, in bytes.</param>
    /// <param name="clumpSize">The clump size, in bytes.</param>
    /// <param name="allocationBlockStart">The starting sector of the first allocation block.</param>
    /// <param name="nextFileNumber">The next file number to be assigned.</param>
    /// <param name="freeAllocationBlocks">The number of free allocation blocks.</param>
    /// <param name="volumeName">The volume name.</param>
    public MfsMasterDirectoryBlock(
        MfsTimestamp creationDate,
        MfsMasterDirectoryBlockAttributes attributes,
        ushort numberOfFiles,
        ushort fileDirectoryStart,
        ushort fileDirectoryLength,
        ushort numberOfAllocationBlocks,
        uint allocationBlockSize,
        uint clumpSize,
        ushort allocationBlockStart,
        uint nextFileNumber,
        ushort freeAllocationBlocks,
        String27 volumeName)
    {
        Signature = 0xD2D7;
        CreationDate = creationDate;
        LastBackupDate = new MfsTimestamp(0);
        Attributes = attributes;
        NumberOfFiles = numberOfFiles;
        FileDirectoryStart = fileDirectoryStart;
        FileDirectoryLength = fileDirectoryLength;
        NumberOfAllocationBlocks = numberOfAllocationBlocks;
        AllocationBlockSize = allocationBlockSize;
        ClumpSize = clumpSize;
        AllocationBlockStart = allocationBlockStart;
        NextFileNumber = nextFileNumber;
        FreeAllocationBlocks = freeAllocationBlocks;
        VolumeName = volumeName;
    }

    /// <summary>
    /// Writes this master directory block to the specified span in big-endian format.
    /// </summary>
    /// <param name="data">The destination span. Must be at least <see cref="Size"/> bytes.</param>
    public readonly void WriteTo(Span<byte> data)
    {
        if (data.Length < Size)
        {
            throw new ArgumentException($"Destination must be at least {Size} bytes long.", nameof(data));
        }

        int offset = 0;

        // drSigWord (word)
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(offset, 2), Signature);
        offset += 2;

        // drCrDate (long word)
        CreationDate.WriteTo(data.Slice(offset, MfsTimestamp.Size));
        offset += MfsTimestamp.Size;

        // drLsBkUp (long word)
        LastBackupDate.WriteTo(data.Slice(offset, MfsTimestamp.Size));
        offset += MfsTimestamp.Size;

        // drAtrb (word)
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(offset, 2), (ushort)Attributes);
        offset += 2;

        // drNmFls (word)
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(offset, 2), NumberOfFiles);
        offset += 2;

        // drDirSt (word)
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(offset, 2), FileDirectoryStart);
        offset += 2;

        // drBILen (word)
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(offset, 2), FileDirectoryLength);
        offset += 2;

        // drNmAIBIks (word)
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(offset, 2), NumberOfAllocationBlocks);
        offset += 2;

        // drAlBlkSiz (long word)
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(offset, 4), AllocationBlockSize);
        offset += 4;

        // drClpSiz (long word)
        BinaryPrimitives.WriteUInt32BigEndian(data[offset..], ClumpSize);
        offset += 4;

        // drAIBISt (word)
        BinaryPrimitives.WriteUInt16BigEndian(data[offset..], AllocationBlockStart);
        offset += 2;

        // drNxtFNum (long word)
        BinaryPrimitives.WriteUInt32BigEndian(data[offset..], NextFileNumber);
        offset += 4;

        // drFreeBks (word)
        BinaryPrimitives.WriteUInt16BigEndian(data[offset..], FreeAllocationBlocks);
        offset += 2;

        // drVN (byte) length of volume name
        int volumeNameLength = VolumeName.Length;
        data[offset] = (byte)volumeNameLength;
        offset += 1;

        // drVN + 1 (bytes) characters of volume name
        VolumeName.AsReadOnlySpan()[..volumeNameLength].CopyTo(data.Slice(offset, String27.Size));
        offset += String27.Size;

        Debug.Assert(offset == Size, "Did not write all Master Directory Block data.");
    }
}
