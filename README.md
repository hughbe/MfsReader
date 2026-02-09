# MfsReader

A lightweight, high-performance .NET library for reading classic Macintosh File System (MFS) disk images and extracting their contents. MFS was the original file system used by early Macintosh computers (1984-1985) before being replaced by HFS.

## Features

- Read MFS disk images (e.g., 400K floppy disk images)
- Write MFS disk images from scratch with custom file entries
- Support for Apple Partition Map (APM) disk images containing MFS partitions
- Enumerate all files in an MFS volume
- Extract both data and resource forks from files
- Access file metadata (name, type, creator, dates, sizes)
- **Zero-allocation parsing** - File and volume metadata parsing allocates no heap memory
- Support for .NET 9.0

## Performance

MfsReader is designed for high performance with zero heap allocations during metadata parsing:

| Operation | Time | Allocated |
|-----------|------|-----------|
| Parse Master Directory Block | ~10 ns | 0 B |
| Parse File Directory Entry | ~28 ns | 0 B |

This is achieved through:
- **InlineArray structs** (`String4`, `String27`, `String255`) for fixed-size strings
- **MfsTimestamp** for deferred timestamp conversion (stores raw `uint`, converts to `DateTime` only on demand)
- **Span-based parsing** for efficient binary data reading
- **ArrayPool** for temporary buffers

## Installation

Add the project reference to your .NET application:

```sh
dotnet add reference path/to/MfsReader.csproj
```

Or, if published on NuGet:

```sh
dotnet add package MfsReader
```

## Usage

### Reading an MFS Disk Image

```csharp
using MfsReader;

// Open a disk image file
using var stream = File.OpenRead("mfs400K.dsk");

// Create an MFS disk reader - automatically detects APM partitions
var disk = new MfsDisk(stream);

// Iterate through all MFS volumes found on the disk
foreach (var volume in disk.Volumes)
{
    var mdb = volume.MasterDirectoryBlock;
    Console.WriteLine($"Volume Name: {mdb.VolumeName}");
    Console.WriteLine($"Created: {mdb.CreationDate}");
    Console.WriteLine($"Last Backup: {mdb.LastBackupDate}");
    
    // Process files in this volume
    foreach (var file in volume.GetEntries())
    {
        Console.WriteLine($"  File: {file.Name}");
    }
}
```

### Enumerating Files

```csharp
// Get all files in each volume
foreach (var volume in disk.Volumes)
{
    var files = volume.GetEntries();

    foreach (var file in files)
    {
        Console.WriteLine($"File: {file.Name}");
        Console.WriteLine($"  Type: {file.FileType}");
        Console.WriteLine($"  Creator: {file.Creator}");
        Console.WriteLine($"  Data Fork: {file.DataForkSize} bytes");
        Console.WriteLine($"  Resource Fork: {file.ResourceForkSize} bytes");
        Console.WriteLine($"  Created: {file.CreationDate}");
        Console.WriteLine($"  Modified: {file.LastModificationDate}");
    }
}
```

### Extracting File Data

```csharp
// Extract data fork
foreach (var volume in disk.Volumes)
{
    foreach (var file in volume.GetEntries())
    {
        // Get data fork as byte array
        byte[] dataFork = volume.GetDataForkData(file);
        File.WriteAllBytes($"{file.Name}.data", dataFork);
        
        // Get resource fork as byte array
        byte[] resourceFork = volume.GetResourceForkData(file);
        File.WriteAllBytes($"{file.Name}.rsrc", resourceFork);
    }
}
```

### Streaming File Data

```csharp
// Stream file data to an output stream
var volume = disk.Volumes.First();
var file = volume.GetEntries().First();

using var outputStream = File.Create("output.bin");
volume.GetDataForkData(file, outputStream);
```

### Writing an MFS Disk Image

```csharp
using MfsReader;

var writer = new MfsDiskWriter();

// Add files with data and/or resource forks
writer.AddFile("Read Me", "TEXT", "ttxt",
    dataFork: File.ReadAllBytes("readme.txt"));

writer.AddFile("MyApp", "APPL", "MYAP",
    dataFork: File.ReadAllBytes("myapp.data"),
    resourceFork: File.ReadAllBytes("myapp.rsrc"));

// Write the disk image to a stream
using var stream = File.Create("output.dsk");
writer.WriteTo(stream, volumeName: "My Disk");
```

## API Overview

### MfsDisk

The main class for reading disks that may contain Apple Partition Map entries with MFS partitions.

- `MfsDisk(Stream stream)` - Opens a disk from a stream and scans for MFS volumes
- `Volumes` - Gets the list of MFS volumes found on the disk

### MfsVolume

The main class for reading MFS volumes.

- `MfsVolume(Stream stream)` - Opens an MFS volume from a stream
- `MasterDirectoryBlock` - Gets the master directory block (volume metadata)
- `AllocationBlockMap` - Gets the allocation block map of the volume
- `GetEntries()` - Enumerates all file entries in the volume
- `GetDataForkData(file)` - Reads the data fork as a byte array
- `GetDataForkData(file, outputStream)` - Streams the data fork to an output stream
- `GetResourceForkData(file)` - Reads the resource fork as a byte array
- `GetResourceForkData(file, outputStream)` - Streams the resource fork to an output stream
- `GetFileData(file, forkType)` - Reads file data as a byte array
- `GetFileData(file, outputStream, forkType)` - Streams file data to an output stream

### MfsDiskWriter

Builder-pattern class for creating MFS disk images.

- `AddFile(name, fileType, creator, dataFork?, resourceFork?, flags?, finderFlags?, folderNumber?)` - Adds a file entry to the disk image
- `WriteTo(stream, volumeName?, allocationBlockSize?)` - Writes the complete MFS disk image to a stream

### MfsMasterDirectoryBlock

Contains volume-level metadata (zero-allocation struct):

- `Signature` - Volume signature (0xD2D7 for MFS)
- `VolumeName` - Name of the volume (`String27` - zero-allocation, implicitly converts to `string`)
- `CreationDate` - Volume creation date (`MfsTimestamp` - zero-allocation, implicitly converts to `DateTime`)
- `LastBackupDate` - Volume last backup date (`MfsTimestamp` - zero-allocation, implicitly converts to `DateTime`)
- `Attributes` - Volume attributes/flags
- `NumberOfFiles` - Number of files in the volume
- `FileDirectoryStart` - Starting sector of the file directory
- `FileDirectoryLength` - Length of the file directory in sectors
- `NumberOfAllocationBlocks` - Number of allocation blocks on the volume
- `AllocationBlockSize` - Size of allocation blocks in bytes
- `ClumpSize` - Clump size in bytes
- `AllocationBlockStart` - Starting sector of the first allocation block
- `NextFileNumber` - Next file number to be assigned
- `FreeAllocationBlocks` - Number of free allocation blocks

### MfsFileDirectoryBlock

Represents a file entry in the MFS volume (zero-allocation struct):

- `Name` - File name (`String255` - zero-allocation, implicitly converts to `string`)
- `Flags` - Entry flags (used, locked)
- `Version` - Version number
- `FileType` - Four-character file type code (`String4` - zero-allocation)
- `Creator` - Four-character creator code (`String4` - zero-allocation)
- `FinderFlags` - Finder flags
- `ParentLocationX` - X-coordinate of file's location in parent
- `ParentLocationY` - Y-coordinate of file's location in parent
- `FolderNumber` - Folder number
- `FileNumber` - File number
- `CreationDate` - File creation date (`MfsTimestamp` - zero-allocation, implicitly converts to `DateTime`)
- `LastModificationDate` - File last modified date (`MfsTimestamp` - zero-allocation, implicitly converts to `DateTime`)
- `DataForkAllocationBlock` - Starting allocation block for data fork
- `DataForkSize` - Size of the data fork in bytes
- `DataForkAllocatedSize` - Allocated size of the data fork in bytes
- `ResourceForkAllocationBlock` - Starting allocation block for resource fork
- `ResourceForkSize` - Size of the resource fork in bytes
- `ResourceForkAllocatedSize` - Allocated size of the resource fork in bytes

### MfsTimestamp

Represents an MFS timestamp as seconds since the MacOS epoch (January 1, 1904 00:00:00 UTC). Zero-allocation struct that avoids converting to `DateTime` during parsing:

- `Value` - Raw timestamp value (seconds since the MacOS epoch)
- `ToDateTime()` - Converts to `DateTime` (UTC)
- Implicit conversion to `DateTime` for seamless use
- Implements `IEquatable<MfsTimestamp>`, `IComparable<MfsTimestamp>`, `IFormattable`

## Building

Build the project using the .NET SDK:

```sh
dotnet build
```

Run tests:

```sh
dotnet test
```

## MfsDumper CLI

A command-line tool for inspecting and extracting MFS disk images.

### Install/Build

```sh
dotnet build dumper/MfsDumper.csproj -c Release
```

### Commands

#### `header` - Dump volume header information

Display the Master Directory Block (MDB) metadata for all volumes on a disk:

```sh
mfs-dumper header /path/to/disk.dsk [-v <volume>]
```

Shows volume name, creation/backup dates, allocation block info, and space usage.

#### `list` - List files in a volume

```sh
mfs-dumper list /path/to/disk.dsk [OPTIONS]
```

Options:
- `-v, --volume <n>` - Select specific volume (1-based index)
- `-b, --brief` - Show only summary table without detailed per-file info
- `-f, --filter <pattern>` - Filter files by name (supports `*` and `?` wildcards)

Example:
```sh
mfs-dumper list disk.dsk --brief --filter "*.txt"
```

#### `map` - Dump allocation block map

Visualize the allocation block map showing which blocks are free, in use, or end-of-file markers:

```sh
mfs-dumper map /path/to/disk.dsk [OPTIONS]
```

Options:
- `-v, --volume <n>` - Select specific volume
- `--used-only` - Show only used allocation blocks
- `--free-only` - Show only free allocation blocks

#### `hexdump` - Hex dump of file fork data

View raw hex dump of a file's data or resource fork:

```sh
mfs-dumper hexdump /path/to/disk.dsk "File Name" [OPTIONS]
```

Options:
- `-v, --volume <n>` - Select specific volume
- `--fork <type>` - Fork to dump: `data` (default), `resource`, or `both`
- `-n, --bytes <n>` - Maximum bytes to dump (default: 256)
- `--offset <n>` - Byte offset to start from (default: 0)

Example:
```sh
mfs-dumper hexdump disk.dsk "Read Me" --fork both -n 512
```

#### `extract` - Extract files from disk image

```sh
mfs-dumper extract /path/to/disk.dsk [OPTIONS]
```

Options:
- `-o, --output <dir>` - Output directory (default: disk image name)
- `-v, --volume <n>` - Extract from specific volume only
- `-f, --filter <pattern>` - Extract only files matching pattern
- `--data-only` - Extract only data forks
- `--resource-only` - Extract only resource forks
- `--preserve-dates` - Preserve original timestamps (default: true)
- `--dry-run` - Preview extraction without writing files

Example:
```sh
mfs-dumper extract disk.dsk -o output/ --filter "*.txt" --dry-run
```

Files are written as `<Name>.data` and `<Name>.res`, with invalid filename characters replaced by `_`.

## Requirements

- .NET 9.0 or later

## License

MIT License. See [LICENSE](LICENSE) for details.

Copyright (c) 2026 Hugh Bellamy

## About MFS

The Macintosh File System (MFS) was the original file system for the Macintosh computer, introduced in 1984. Key characteristics:

- Flat file structure (no subdirectories/folders)
- Support for data and resource forks
- Maximum of 128 files per volume
- Primarily used on 400K floppy disks
- Replaced by HFS (Hierarchical File System) in 1985

## Related Projects

- [AppleDiskImageReader](https://github.com/hughbe/AppleDiskImageReader) - Reader for Apple II universal disk (.2mg) images
- [AppleIIDiskReader](https://github.com/hughbe/AppleIIDiskReader) - Reader for Apple II DOS 3.3 disk (.dsk) images
- [ProDosVolumeReader](https://github.com/hughbe/ProDosVolumeReader) - Reader for ProDOS (.po) volumes
- [WozDiskImageReader](https://github.com/hughbe/WozDiskImageReader) - Reader for WOZ (.woz) disk images
- [DiskCopyReader](https://github.com/hughbe/DiskCopyReader) - Reader for Disk Copy 4.2 (.dc42) images
- [MfsReader](https://github.com/hughbe/MfsReader) - Reader for MFS (Macintosh File System) volumes
- [HfsReader](https://github.com/hughbe/HfsReader) - Reader for HFS (Hierarchical File System) volumes
- [ApplePartitionMapReader](https://github.com/hughbe/ApplePartitionMapReader) - Reader for Apple Partition Map (APM) images
- [ResourceForkReader](https://github.com/hughbe/ResourceForkReader) - Reader for Macintosh resource forks
- [BinaryIIReader](https://github.com/hughbe/BinaryIIReader) - Reader for Binary II (.bny, .bxy) archives
- [StuffItReader](https://github.com/hughbe/StuffItReader) - Reader for StuffIt (.sit) archives
- [ShrinkItReader](https://github.com/hughbe/ShrinkItReader) - Reader for ShrinkIt (.shk, .sdk) archives
