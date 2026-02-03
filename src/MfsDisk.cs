using ApplePartitionMapReader;

namespace MfsReader;

/// <summary>
/// Represents a disk containing one or more MFS volumes.
/// </summary>
public class MfsDisk
{
    /// <summary>
    /// Gets the list of MFS volumes found on the disk.
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
            var partitionMap = new ApplePartitionMap(stream, 0);
            foreach (var partitionEntry in partitionMap.Entries)
            {
                if (partitionEntry.Type == ApplePartitionMapIdentifiers.AppleMFS)
                {
                    // Found the MFS partition - add a volume for it.
                    var mfsStartOffset = (long)partitionEntry.PartitionStartBlock * 512;
                    stream.Seek(mfsStartOffset, SeekOrigin.Begin);
                    Volumes.Add(new MfsVolume(stream));
                }
            }
        }

        // If no MFS volumes found, assume the entire image is a single MFS volume.
        if (Volumes.Count == 0)
        {
            stream.Seek(0, SeekOrigin.Begin);
            Volumes.Add(new MfsVolume(stream));
        }
    }
}
