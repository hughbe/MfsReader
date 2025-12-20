using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using MfsReader.Utilities;

namespace MfsReader;

/// <summary>
/// Represents a file directory block in an MFS volume.
/// </summary>
public struct MFSFileDirectoryBlock
{
    /// <summary>
    /// Gets the minimum size of a file directory block, in bytes.
    /// </summary>
    public const int MinSize = 51;

    /// <summary>
    /// Gets the flags of the file directory block.
    /// </summary>
    public MFSFileDirectoryBlockFlags Flags { get; }

    /// <summary>
    /// Gets the version number of the file directory block.
    /// </summary>
    public byte Version { get; }

    /// <summary>
    /// Gets the file type.
    /// </summary>
    public string FileType { get; }

    /// <summary>
    /// Gets the file creator.
    /// </summary>
    public string Creator { get; }

    /// <summary>
    /// Gets the Finder flags.
    /// </summary>
    public ushort FinderFlags { get; }

    /// <summary>
    /// Gets the X-coordinate of the file's location within the parent.
    /// </summary>
    public ushort ParentLocationX { get; }

    /// <summary>
    /// Gets the Y-coordinate of the file's location within the parent.
    /// </summary>
    public ushort ParentLocationY { get; }

    /// <summary>
    /// Gets the folder number.
    /// </summary>
    public short FolderNumber { get; }

    /// <summary>
    /// Gets the file number.
    /// </summary>
    public uint FileNumber { get; }

    /// <summary>
    /// Gets the first allocation block of the data fork.
    /// </summary>
    public ushort DataForkAllocationBlock { get; }

    /// <summary>
    /// Gets the size of the data fork in bytes.
    /// </summary>
    public uint DataForkSize { get; }

    /// <summary>
    /// Gets the allocated size of the data fork in bytes.
    /// </summary>
    public uint DataForkAllocatedSize { get; }

    /// <summary>
    /// Gets the first allocation block of the resource fork.
    /// </summary>
    public ushort ResourceForkAllocationBlock { get; }

    /// <summary>
    /// Gets the size of the resource fork in bytes.
    /// </summary>
    public uint ResourceForkSize { get; }

    /// <summary>
    /// Gets the allocated size of the resource fork in bytes.
    /// </summary>
    public uint ResourceForkAllocatedSize { get; }

    /// <summary>
    /// Gets the creation date of the file.
    /// </summary>
    public DateTime CreationDate { get; }

    /// <summary>
    /// Gets the last modification date of the file.
    /// </summary>
    public DateTime LastModificationDate { get; }

    /// <summary>
    /// Gets the name of the file.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MFSFileDirectoryBlock"/> struct.
    /// </summary>
    /// <param name="data">The span containing the file directory block data.</param>
    public MFSFileDirectoryBlock(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinSize)
        {
            throw new ArgumentException("Data span is too small to contain a valid MFS file directory block.", nameof(data));
        }

        int offset = 0;

        // fIFIags (byte) bit 7 = 1 if entry U3ed; bit 0 = 1 if file locked
        // Flags. Bit 7 is always set in a directory entry; when clear, it indicates empty space with no further directory entries in this block. Bit 0 is set if the file is locked. Other bits are unknown.
        Flags = (MFSFileDirectoryBlockFlags)data[offset];
        offset += 1;

        // fITyp (byte) version number
        // Version. Always 0x00.
        Version = data[offset];
        if (Version != 0x00)
        {
            throw new ArgumentException("Unsupported MFS file directory block version.", nameof(data));
        }

        offset += 1;

        // flUsrWds (16 byte3) information U3ed by the Finder
        // File type. This is often (but not always) a readable 4-character string. When the type is unknown, it should be set to 0x3F3F3F3F.
        FileType = Encoding.ASCII.GetString(data.Slice(offset, 4));
        offset += 4;

        // File creator. This is often (but not always) a readable 4-character string. When the appropriate application to open the file is unknown, it should be set to 0x3F3F3F3F.
        Creator = Encoding.ASCII.GetString(data.Slice(offset, 4));
        offset += 4;

        // Finder flags. Some of these flags mean different things
        // depending on the Finder version, so they're not documented here.
        // Applications may require some of these bits to be set, but
        // most other files do not.
        FinderFlags = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;

        // Position. This determines where in the window the file's
        // icon will be displayed. This may safely be set to 0 when
        // it's not needed.
        ParentLocationX = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;

        ParentLocationY = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;

        // Folder number. This determines which window the file will be
        // displayed in. The value 0 means the main volume window, -2
        // means the desktop, and -3 means the trash. A positive value
        // is the ID number for a folder.
        FolderNumber = BinaryPrimitives.ReadInt16BigEndian(data[offset..]);
        offset += 2;

        // flFINum (long word) file number
        // File number. This number must be unique for each file in the volume.
        FileNumber = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;

        // fIStBlk (word) first allocation block of data fork
        // First data fork allocation block. If the data fork has no allocation blocks, this should be 0.
        DataForkAllocationBlock = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;

        // fILgLen (long word) logical end-of-file of data fork
        // Data fork size, in bytes.
        DataForkSize = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;

        // flPyLen (long word) physical end-of-file of data fork 
        // Data fork allocated space, in bytes. This is the total size of all the allocation blocks belonging to the data fork, including any space that isn't used.
        DataForkAllocatedSize = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;

        // fIRStBlk (word) first allocation block of resource fork
        // First resource fork allocation block. If the resource fork has no allocation blocks, this should be 0.
        ResourceForkAllocationBlock = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;

        // Resource fork size, in bytes.
        // fIRLgLen (long word) logical end-of-file of resource fork 
        ResourceForkSize = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;

        // Resource fork allocated space, in bytes. This is the total size of all the allocation blocks belonging to the resource fork, including any space that isn't used.
        // fIRPyLen (long word) physical end-of-file of resource fork 
        ResourceForkAllocatedSize = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;

        // fICrDat (long word) date and time of creation
        // Creation date, in seconds since midnight January 1st 1904.
        CreationDate = SpanUtilities.ReadMacOSTimestamp(data[offset..]);
        offset += 4;

        // flMdDat (long word) date and time of last modification
        // Modification date, in seconds since midnight January 1st 1904.
        LastModificationDate = SpanUtilities.ReadMacOSTimestamp(data[offset..]);
        offset += 4;

        // flNam (byte) length of file name followed by file name
        // flNam + 1 (bytes) characters of file name
        // File name. This field has a variable length and contains a
        // Pascal string. The first byte indicates the number of characters
        // in the string, and that many of the following bytes contain the
        // string.
        Name = SpanUtilities.ReadPascalString(data[offset..]);
        offset += 1 + Name.Length;

        Debug.Assert(offset == MinSize + Name.Length);
    }
}