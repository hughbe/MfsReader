# MfsReader

A lightweight .NET library for reading classic Macintosh File System (MFS) disk images and extracting their contents. MFS was the original file system used by early Macintosh computers (1984-1985) before being replaced by HFS.

## Features

- Read MFS disk images (e.g., 400K floppy disk images)
- Enumerate all files in an MFS volume
- Extract both data and resource forks from files
- Access file metadata (name, type, creator, dates, sizes)
- Support for .NET 9.0
- Zero external dependencies

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

// Create an MFS volume reader
var volume = new MFSVolume(stream);

// Get volume information
var mdb = volume.MasterDirectoryBlock;
Console.WriteLine($"Volume Name: {mdb.VolumeName}");
Console.WriteLine($"Created: {mdb.CreateDate}");
Console.WriteLine($"Modified: {mdb.ModifyDate}");
```

### Enumerating Files

```csharp
// Get all files in the volume
var files = volume.GetEntries();

foreach (var file in files)
{
    Console.WriteLine($"File: {file.Name}");
    Console.WriteLine($"  Type: {file.FileType}");
    Console.WriteLine($"  Creator: {file.FileCreator}");
    Console.WriteLine($"  Data Fork: {file.DataForkSize} bytes");
    Console.WriteLine($"  Resource Fork: {file.ResourceForkSize} bytes");
    Console.WriteLine($"  Created: {file.CreateDate}");
    Console.WriteLine($"  Modified: {file.ModifyDate}");
}
```

### Extracting File Data

```csharp
// Extract data fork
foreach (var file in volume.GetEntries())
{
    // Get data fork as byte array
    byte[] dataFork = volume.GetFileData(file, resourceFork: false);
    File.WriteAllBytes($"{file.Name}.data", dataFork);
    
    // Get resource fork as byte array
    byte[] resourceFork = volume.GetFileData(file, resourceFork: true);
    File.WriteAllBytes($"{file.Name}.rsrc", resourceFork);
}
```

### Streaming File Data

```csharp
// Stream file data to an output stream
var file = volume.GetEntries().First();

using var outputStream = File.Create("output.bin");
volume.GetFileData(file, outputStream, resourceFork: false);
```

## API Overview

### MFSVolume

The main class for reading MFS volumes.

- `MFSVolume(Stream stream)` - Opens an MFS volume from a stream
- `MasterDirectoryBlock` - Gets the master directory block (volume metadata)
- `GetEntries()` - Enumerates all file entries in the volume
- `GetFileData(file, resourceFork)` - Reads file data as a byte array
- `GetFileData(file, outputStream, resourceFork)` - Streams file data to an output stream

### MFSMasterDirectoryBlock

Contains volume-level metadata:

- `VolumeName` - Name of the volume
- `CreateDate` - Volume creation date
- `ModifyDate` - Volume last modified date
- `VolumeAttributes` - Volume attributes/flags
- `FileCount` - Number of files in the volume
- `FileDirectoryStart` - Starting block of the file directory
- `FileDirectoryLength` - Length of the file directory in blocks
- `AllocationBlockSize` - Size of allocation blocks in bytes

### MFSFileDirectoryBlock

Represents a file entry in the MFS volume:

- `Name` - File name (up to 255 characters)
- `FileType` - Four-character file type code
- `FileCreator` - Four-character creator code
- `CreateDate` - File creation date
- `ModifyDate` - File last modified date
- `DataForkSize` - Size of the data fork in bytes
- `ResourceForkSize` - Size of the resource fork in bytes
- `DataForkAllocationBlock` - Starting allocation block for data fork
- `ResourceForkAllocationBlock` - Starting allocation block for resource fork

## Building

Build the project using the .NET SDK:

```sh
dotnet build
```

Run tests:

```sh
dotnet test
```

## MFSDumper CLI

Extract an MFS disk image to a directory using the dumper tool.

### Install/Build

```sh
dotnet build dumper/MFSDumper.csproj -c Release
```

### Usage

```sh
MFSDumper \
    /path/to/disk.dsk \
    -o /path/to/output \
    [--data-only | --resource-only]
```

- Input: Path to the `.dsk` image.
- Output: Destination directory for extracted files.
- Fork selection:
    - `--data-only`: Extract only data forks.
    - `--resource-only`: Extract only resource forks.

Files are written as `<Name>.data` and `<Name>.res`, with `/` and `:` replaced by `_` for compatibility.

## Requirements

- .NET 9.0 or later

## License

MIT License. See [LICENSE](LICENSE) for details.

Copyright (c) 2025 Hugh Bellamy

## About MFS

The Macintosh File System (MFS) was the original file system for the Macintosh computer, introduced in 1984. Key characteristics:

- Flat file structure (no subdirectories/folders)
- Support for data and resource forks
- Maximum of 128 files per volume
- Primarily used on 400K floppy disks
- Replaced by HFS (Hierarchical File System) in 1985

## Related Projects

- [HfsReader](https://github.com/hughbe/HfsReader) - Reader for HFS (Hierarchical File System) volumes
