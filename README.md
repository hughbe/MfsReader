# MfsReader

A lightweight, high-performance .NET library for reading classic Macintosh File System (MFS) disk images and extracting their contents. MFS was the original file system used by early Macintosh computers (1984-1985) before being replaced by HFS.

## Features

- Read MFS disk images (e.g., 400K floppy disk images)
- Write MFS disk images from scratch with custom file entries (including empty volumes)
- Tolerant reader that follows the behaviour of the Macintosh File Manager (see [Format notes](#format-notes))
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
        File.WriteAllBytes($"{file.Name}.res", resourceFork);
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
    resourceFork: File.ReadAllBytes("myapp.res"));

// Write the disk image to a stream
using var stream = File.Create("output.dsk");
writer.WriteTo(stream, volumeName: "My Disk");
```

## API Overview

### MfsDisk

The main class for reading disks that may contain Apple Partition Map entries with MFS partitions.

- `MfsDisk(Stream stream)` - Opens a disk from a stream and scans for MFS volumes
- `Volumes` - Gets the list of MFS volumes found on the disk (empty if the disk has a partition map with no `Apple_MFS` partition)

### MfsVolume

The main class for reading MFS volumes.

- `MfsVolume(Stream stream)` - Opens an MFS volume that starts at the stream's **current position** and runs to its end (position the stream at 0 for a bare disk image)
- `MasterDirectoryBlock` - Gets the master directory block (volume metadata)
- `AllocationBlockMap` - Gets the allocation block map of the volume
- `GetEntries()` - Enumerates the used file entries in the volume. Returns an `MfsDirectoryEntries` struct whose `foreach` enumerator also lives on the stack, so enumerating allocates nothing (it still implements `IEnumerable<MfsFileDirectoryBlock>`, so LINQ and `ToList()` work). Follows the Mac File Manager: a zero flags byte ends the entries in a directory sector, and the master directory block's file count is only a hint
- `GetDataForkData(file)` - Reads the data fork as a byte array
- `GetDataForkData(file, outputStream)` - Streams the data fork to an output stream
- `GetResourceForkData(file)` - Reads the resource fork as a byte array
- `GetResourceForkData(file, outputStream)` - Streams the resource fork to an output stream
- `GetFileData(file, forkType)` - Reads file data as a byte array
- `GetFileData(file, outputStream, forkType)` - Streams file data to an output stream

### MfsDiskWriter

Builder-pattern class for creating MFS disk images.

- `AddFile(name, fileType, creator, dataFork?, resourceFork?, flags?, finderFlags?, folderNumber?)` - Adds a file entry to the disk image. Names must be unique, ignoring case
- `WriteTo(stream, volumeName?, allocationBlockSize?)` - Writes the complete MFS disk image to a stream (an empty volume if no files were added). Timestamps are the current local time

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

Represents an MFS timestamp as seconds since midnight, January 1, 1904. Classic Mac OS stores the machine's local wall-clock time with no time zone, so the value is *not* UTC. Zero-allocation struct that avoids converting to `DateTime` during parsing:

- `Value` - Raw timestamp value (seconds since the MacOS epoch)
- `ToDateTime()` - Converts to a `DateTime` of `DateTimeKind.Unspecified` holding the wall-clock time (do not call `ToLocalTime()` on it)
- `FromDateTime(dateTime)` - Local and unspecified values are used as wall-clock time; UTC values are converted to local time first
- Implicit conversion to `DateTime` for seamless use
- Implements `IEquatable<MfsTimestamp>`, `IComparable<MfsTimestamp>`, `IFormattable`

## Format notes

The reader aims to behave like the original Macintosh File Manager rather than reject anything unusual, so that one damaged structure doesn't make the rest of a disk unreadable. These behaviours are based on Inside Macintosh Volume II, Apple's published ROM source for the MFS driver, and the [CiderPress II MFS notes](https://ciderpress2.com/formatdoc/MFS-notes.html).

**Reading**

- **Where a volume starts.** `new MfsVolume(stream)` treats the stream's *current position* as the start of the volume. A stream holding a bare disk image must be at position 0. `MfsDisk` handles this for you, including Apple Partition Map partitions. If a disk has a partition map but no `Apple_MFS` partition, `MfsDisk.Volumes` is empty (the image is not misread as a bare MFS volume).
- **Directory entries.** Within each 512-byte directory sector, a zero flags byte ends the entries, as in the ROM (which keeps entries packed and zeroes the rest of the sector when a file is deleted). An entry with a non-zero flags byte but without the "entry used" bit (bit 7) is skipped rather than reported.
- **Hints, not rules.** The file count in the master directory block is a hint (the ROM corrects it when it disagrees with the directory), and the per-entry version byte is preserved in `Version` but never rejected.
- **Allocation block map.** Entries are 12 bits, numbered from block 2. `0` is free, `1` ends a chain, and `0xFFF` is reserved (it marks blocks that hold the file directory when the directory sits inside the allocation area, which the reader supports). Entry values are only checked when a chain is followed, so a stray value in an unused entry doesn't stop the volume opening. Reading a file whose chain points outside the volume, loops, or ends early throws `InvalidDataException`. When streaming a fork to an output stream, the data that precedes the damage has already been written when the exception is thrown, so partial recovery is possible.
- **Untrusted sizes.** A fork size larger than the volume's whole allocation area is rejected with `InvalidDataException` before any buffer is allocated, so a corrupt directory entry can't make a tiny image request gigabytes.
- **Reading is fast and thread safe.** Contiguous allocation blocks are read with a single seek and read, and forks are read straight into the result array. Reads from one `MfsVolume` may run on several threads at once; the volume serialises access to the stream, which must not be used elsewhere while the volume is in use.
- **Errors.** A malformed or truncated volume throws `InvalidDataException`. If the stream shrinks after the volume has been opened, the underlying `EndOfStreamException` is surfaced instead.

**Timestamps**

Dates are seconds since midnight, 1 January 1904 in the **local time** of the Mac that wrote them; there is no time zone. `MfsTimestamp.ToDateTime()` therefore returns a `DateTimeKind.Unspecified` value holding that wall-clock time. It is not UTC, so don't call `ToLocalTime()` on it.

**Writing**

- File names must be unique on the volume, ignoring case (`AddFile` throws `ArgumentException` otherwise).
- A volume can use at most **4093** allocation blocks. Block numbers start at 2 and `0xFFF` is reserved, so the highest usable block is `0xFFE`. Pick a larger `allocationBlockSize` for larger content.
- Writing with no files produces a valid empty volume.
- Created and modified times are set to the current local time.
- The output is exactly as large as its contents. It is not padded to a standard 400K/800K floppy size.

## Building

Build the project using the .NET SDK:

```sh
dotnet build
```

Run tests:

```sh
dotnet test
```

Collect line and branch coverage with Coverlet:

```sh
dotnet test --collect:"XPlat Code Coverage" --settings tests/coverlet.runsettings
```

The Cobertura report is written under `tests/TestResults`.

## MfsDumper CLI

A command-line tool for inspecting and extracting MFS disk images.

### Install/Build

```sh
dotnet build dumper/MFSDumper.csproj -c Release
```

The examples below show the command as `mfs-dumper`. The built executable is named `MFSDumper`; you can also run any command straight from source, e.g.:

```sh
dotnet run --project dumper -- list /path/to/disk.dsk
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

- Flat file structure (no real subdirectories; the Finder showed folders using a per-file folder number)
- Support for data and resource forks
- File names up to 255 characters, though Inside Macintosh recommends keeping them to 31 for compatibility with the 128K ROM
- The number of files is limited by the size of the file directory, and the allocation block map uses 12-bit entries, which caps a volume at a few thousand allocation blocks
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
