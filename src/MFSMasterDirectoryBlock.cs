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
    public DateTime CreationDate { get; }

    /// <summary>
    /// Gets the last backup date of the volume.
    /// </summary>
    public DateTime LastBackupDate { get; }

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
        CreationDate = SpanUtilities.ReadMacOSTimestamp(data.Slice(offset, 4));
        offset += 4;

        // drLsBkUp (long word) date and time of last backup
        // Last backup date, in seconds since midnight January 1st 1904.
        LastBackupDate = SpanUtilities.ReadMacOSTimestamp(data.Slice(offset, 4));
        offset += 4;

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

        // drVN (byte) length of volume name.
        // drVN + 1 (bytes) characters of volume name
        // Volume name. This field has a fixed length, but it contains a
        // variable-length Pascal string. The first byte indicates the
        // number of characters in the string, and that many of the following
        // bytes contain the string. Any remaining bytes should be padded with
        // zeroes.
        VolumeName = SpanUtilities.ReadPascalString27(data[offset..(offset + 28)]);
        offset += 28;

        Debug.Assert(offset == data.Length, "Did not read all Master Directory Block data.");
    }
}
