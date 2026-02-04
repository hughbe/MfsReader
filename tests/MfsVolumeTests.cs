using System.Diagnostics;

namespace MfsReader.Tests;

public class MfsVolumeTests
{
    [Theory]
    [InlineData("mfs400K.dsk")]
    [InlineData("mfs800K.dsk")]
    [InlineData("mfs1440K.dsk")]
    [InlineData("SystemAdditions.dsk")]
    [InlineData("MacSpeak.dsk")]
    public void Ctor_Stream(string diskName)
    {
        var filePath = Path.Combine("Samples", diskName);
        using var stream = File.OpenRead(filePath);
        var disk = new MfsDisk(stream);

        Assert.NotEmpty(disk.Volumes);
        foreach (var volume in disk.Volumes)
        {
            var contents = volume.GetEntries().ToList();
            Debug.WriteLine($"Found {contents.Count} items in volume:");
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
    }

    [Fact]
    public void Ctor_NullStream_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>("stream", () => new MfsDisk(null!));
    }

    [Fact]
    public void Ctor_HfsStream_ThrowsInvalidDataException()
    {
        var filePath = Path.Combine("Samples", "hfs", "hfs400K.dsk");
        using var stream = File.OpenRead(filePath);
        Assert.Throws<InvalidDataException>(() => new MfsDisk(stream));
    }

    private static void ExportFile(MfsVolume volume, MfsFileDirectoryBlock entry, string path)
    {
        // Ensure the output directory exists
        Directory.CreateDirectory(path);

        // Sanitize file names for filesystem compatibility
        var safeName = SanitizeName(entry.Name);
        var filePath = Path.Combine(path, safeName);
        
        if (entry.DataForkSize != 0)
        {
            using var outputStream = File.Create(filePath + ".data");
            volume.GetFileData(entry, outputStream, MfsForkType.DataFork);
        }

        if (entry.ResourceForkSize != 0)
        {
            using var outputStream = File.Create(filePath + ".res");
            volume.GetFileData(entry, outputStream, MfsForkType.ResourceFork);
        }
    }

    private static string SanitizeName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var invalidChar in invalidChars)
        {
            name = name.Replace(invalidChar, '_');
        }

        return name;
    }
}
