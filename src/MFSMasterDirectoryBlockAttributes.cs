namespace MfsReader;

/// <summary>
/// Defines the attributes of an MFS master directory block.
/// </summary>
[Flags]
public enum MfsMasterDirectoryBlockAttributes : ushort
{
    /// <summary>
    /// Set if volume is locked by hardware.
    /// </summary>
    LockedByHardware = 0x0080,

    /// <summary>
    /// Set if volume is locked by software.
    /// </summary>
    LockedBySoftware = 0x8000
}