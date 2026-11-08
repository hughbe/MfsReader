using ApplePartitionMapReader;

namespace MfsReader;

/// <summary>
/// Represents a disk containing one or more MFS volumes.
/// </summary>
public class MfsDisk
{
    /// <summary>
    /// Gets the list of MFS volumes found on the disk. This is empty if the disk has an
    /// Apple Partition Map that contains no Apple_MFS partitions.
    /// </summary>
    public List<MfsVolume> Volumes { get; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="MfsDisk"/> class and scans for MFS volumes.
    /// </summary>
    /// <param name="stream">The stream containing the disk image data.</param>
    public MfsDisk(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("Stream must be seekable and readable.", nameof(stream));
        }

        // Try to read Apple Partition Map entries first.
        if (ApplePartitionMap.IsApplePartitionMap(stream, 0))
        {
            // A partitioned disk is described by its map: only Apple_MFS partitions are
            // volumes. If there are none (for example an HFS-only disk), Volumes stays empty
            // rather than misreading the start of the disk as an MFS volume.
            var partitionMap = new ApplePartitionMap(stream, 0);
            foreach (var partitionEntry in partitionMap)
            {
                if (partitionEntry.Type.Equals("Apple_MFS"u8))
                {
                    // Found the MFS partition - add a volume for it.
                    var mfsStartOffset = checked((long)partitionEntry.PartitionStartBlock * VolumeLayout.SectorSize);
                    var mfsLength = checked((long)partitionEntry.PartitionBlockCount * VolumeLayout.SectorSize);
                    stream.Seek(mfsStartOffset, SeekOrigin.Begin);
                    Volumes.Add(new MfsVolume(stream, mfsLength));
                }
            }

            return;
        }

        // Not partitioned: the entire image is a single MFS volume.
        stream.Seek(0, SeekOrigin.Begin);
        Volumes.Add(new MfsVolume(stream));
    }
}
