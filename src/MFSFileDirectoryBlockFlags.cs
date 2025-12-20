namespace MfsReader;

/// <summary>
/// Defines the flags of an MFS file directory block.
/// </summary>
public enum MFSFileDirectoryBlockFlags : byte
{
    /// <summary>
    /// Set if the entry is locked.
    /// </summary>
    Locked = 0x01,

    /// <summary>
    /// Set if the entry is used.
    /// </summary>
    EntryUsed = 0x80,
}