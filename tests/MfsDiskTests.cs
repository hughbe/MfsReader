using System.Diagnostics;

namespace MfsReader.Tests;

public class MfsDiskTests
{
    [Theory]
    [InlineData("mfs400K.dsk")]
    [InlineData("mfs800K.dsk")]
    [InlineData("mfs1440K.dsk")]
    [InlineData("SystemAdditions.dsk")]
    [InlineData("SystemStartup.dsk")]
    [InlineData("MacSpeak.dsk")]
    [InlineData("APM/combined.dsk")]
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
    public void ApmCombinedDisk_ContainsSystemStartupVolume()
    {
        using var combinedStream = File.OpenRead(Path.Combine("Samples", "APM", "combined.dsk"));
        var combinedDisk = new MfsDisk(combinedStream);

        using var systemStartupStream = File.OpenRead(Path.Combine("Samples", "SystemStartup.dsk"));
        var systemStartupDisk = new MfsDisk(systemStartupStream);

        Assert.Single(systemStartupDisk.Volumes);
        var expectedVolume = systemStartupDisk.Volumes[0];
        var expectedEntries = expectedVolume.GetEntries().ToList();

        // Find the matching volume in the APM disk by volume name.
        var expectedVolumeName = expectedVolume.MasterDirectoryBlock.VolumeName.ToString();
        var matchingVolume = combinedDisk.Volumes.Single(v =>
            v.MasterDirectoryBlock.VolumeName.ToString() == expectedVolumeName);

        var actualEntries = matchingVolume.GetEntries().ToList();
        Assert.Equal(expectedEntries.Count, actualEntries.Count);

        for (int i = 0; i < expectedEntries.Count; i++)
        {
            var expected = expectedEntries[i];
            var actual = actualEntries[i];

            Assert.Equal(expected.Name.ToString(), actual.Name.ToString());
            Assert.Equal(expected.FileType.ToString(), actual.FileType.ToString());
            Assert.Equal(expected.Creator.ToString(), actual.Creator.ToString());
            Assert.Equal(expected.DataForkSize, actual.DataForkSize);
            Assert.Equal(expected.ResourceForkSize, actual.ResourceForkSize);

            Assert.Equal(
                expectedVolume.GetDataForkData(expected),
                matchingVolume.GetDataForkData(actual));
            Assert.Equal(
                expectedVolume.GetResourceForkData(expected),
                matchingVolume.GetResourceForkData(actual));
        }
    }

    [Fact]
    public void WriteTo_Roundtrip_InputFiles()
    {
        // Discover all input files, grouping .data and .res by base name.
        var inputDir = "Inputs";
        var inputFiles = Directory.GetFiles(inputDir);
        var fileGroups = new Dictionary<string, (byte[]? dataFork, byte[]? resourceFork)>();

        foreach (var file in inputFiles)
        {
            var fileName = Path.GetFileName(file);
            string baseName;
            bool isData;

            if (fileName.EndsWith(".data"))
            {
                baseName = fileName[..^".data".Length];
                isData = true;
            }
            else if (fileName.EndsWith(".res"))
            {
                baseName = fileName[..^".res".Length];
                isData = false;
            }
            else
            {
                continue;
            }

            if (!fileGroups.TryGetValue(baseName, out var group))
            {
                group = (null, null);
            }

            var data = File.ReadAllBytes(file);
            fileGroups[baseName] = isData ? (data, group.resourceFork) : (group.dataFork, data);
        }

        Assert.NotEmpty(fileGroups);

        // Write a disk image with all the input files.
        var writer = new MfsDiskWriter();
        var sortedNames = fileGroups.Keys.OrderBy(k => k).ToList();
        foreach (var name in sortedNames)
        {
            var (dataFork, resourceFork) = fileGroups[name];
            writer.AddFile(name, "????", "????", dataFork, resourceFork);
        }

        using var diskStream = new MemoryStream();
        writer.WriteTo(diskStream, "TestVolume");

        // Read the disk image back and verify all entries.
        diskStream.Position = 0;
        var disk = new MfsDisk(diskStream);
        Assert.Single(disk.Volumes);

        var volume = disk.Volumes[0];
        Assert.Equal("TestVolume", volume.MasterDirectoryBlock.VolumeName.ToString());

        var entries = volume.GetEntries().ToList();
        Assert.Equal(sortedNames.Count, entries.Count);

        for (int i = 0; i < sortedNames.Count; i++)
        {
            var expectedName = sortedNames[i];
            var entry = entries[i];
            var (expectedDataFork, expectedResourceFork) = fileGroups[expectedName];

            Assert.Equal(expectedName, entry.Name.ToString());

            // Verify data fork.
            if (expectedDataFork != null)
            {
                Assert.Equal((uint)expectedDataFork.Length, entry.DataForkSize);
                Assert.Equal(expectedDataFork, volume.GetDataForkData(entry));
            }
            else
            {
                Assert.Equal(0u, entry.DataForkSize);
            }

            // Verify resource fork.
            if (expectedResourceFork != null)
            {
                Assert.Equal((uint)expectedResourceFork.Length, entry.ResourceForkSize);
                Assert.Equal(expectedResourceFork, volume.GetResourceForkData(entry));
            }
            else
            {
                Assert.Equal(0u, entry.ResourceForkSize);
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
