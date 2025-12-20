using System.Diagnostics;

namespace MfsReader.Tests;

public class MfsVolumeTests
{
    [Theory]
    [InlineData("mfs400K.dsk")]
    [InlineData("mfs800K.dsk")]
    [InlineData("mfs1440K.dsk")]
    public void Ctor_Stream(string diskName)
    {
        var filePath = Path.Combine("Samples", diskName);
        using var stream = File.OpenRead(filePath);
        var volume = new MFSVolume(stream);

        var contents = volume.GetEntries().ToList();
        Debug.WriteLine($"Found {contents.Count} items in root:");
        foreach (var entry in contents)
        {
            Debug.WriteLine($"- {entry.Name} (Data: {entry.DataForkSize} bytes, Resource: {entry.ResourceForkSize} bytes)");
        }

        var outputPath = Path.Combine("Output", Path.GetFileNameWithoutExtension(diskName));
        foreach (var entry in contents)
        {
            ExportFile(volume, entry, outputPath);
        }
    }

    [Fact]
    public void Ctor_NullStream_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>("stream", () => new MFSVolume(null!));
    }

    private static void ExportFile(MFSVolume volume, MFSFileDirectoryBlock entry, string path)
    {
        // Ensure the output directory exists
        Directory.CreateDirectory(path);

        // Sanitize file names for filesystem compatibility
        var safeName = entry.Name.Replace("/", "_").Replace(":", "_");
        var filePath = Path.Combine(path, safeName);
        
        if (entry.DataForkSize != 0)
        {
            using var outputStream = File.Create(filePath + ".data");
            volume.GetFileData(entry, outputStream, false);
        }

        if (entry.ResourceForkSize != 0)
        {
            using var outputStream = File.Create(filePath + ".res");
            volume.GetFileData(entry, outputStream, true);
        }
    }
}
