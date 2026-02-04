using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.RegularExpressions;
using MfsReader;

public sealed class Program
{
    public static int Main(string[] args)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("mfs-dumper");
            config.ValidateExamples();

            config.AddCommand<HeaderCommand>("header")
                .WithDescription("Dump the master directory block (header) information.");

            config.AddCommand<ListCommand>("list")
                .WithDescription("List all file directory entries in the volume.");

            config.AddCommand<MapCommand>("map")
                .WithDescription("Dump the allocation block map.");

            config.AddCommand<HexDumpCommand>("hexdump")
                .WithDescription("Hex dump of a file's fork data.");

            config.AddCommand<ExtractCommand>("extract")
                .WithDescription("Extract files (data and/or resource forks) from the disk image.");
        });

        return app.Run(args);
    }
}

class InputSettings : CommandSettings
{
    [CommandArgument(0, "<input>")]
    [Description("Path to the MFS disk image file.")]
    public required string Input { get; init; }

    [CommandOption("-v|--volume")]
    [Description("Volume index (1-based) to operate on. Defaults to all volumes.")]
    public int? VolumeIndex { get; init; }
}

sealed class HeaderCommand : AsyncCommand<InputSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, InputSettings settings, CancellationToken cancellationToken)
    {
        var input = new FileInfo(settings.Input);
        if (!input.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Input file not found[/]: {input.FullName}");
            return Task.FromResult(-1);
        }

        using var stream = input.OpenRead();
        var disk = new MfsDisk(stream);

        AnsiConsole.MarkupLine($"[green]Found[/] {disk.Volumes.Count} volume(s) on disk.");
        AnsiConsole.WriteLine();

        var volumes = GetSelectedVolumes(disk, settings.VolumeIndex);
        if (volumes == null)
            return Task.FromResult(-1);

        int volumeIndex = settings.VolumeIndex ?? 1;
        foreach (var volume in volumes)
        {
            var mdb = volume.MasterDirectoryBlock;

            AnsiConsole.MarkupLine($"[bold underline]Volume {volumeIndex}: {mdb.VolumeName}[/]");
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("Field");
            table.AddColumn("Value");
            table.Border = TableBorder.Rounded;

            table.AddRow("Signature", $"0x{mdb.Signature:X4}");
            table.AddRow("Volume Name", mdb.VolumeName);
            table.AddRow("Creation Date", mdb.CreationDate.ToString("yyyy-MM-dd HH:mm:ss"));
            table.AddRow("Last Backup Date", mdb.LastBackupDate.ToString("yyyy-MM-dd HH:mm:ss"));
            table.AddRow("Attributes", mdb.Attributes.ToString());
            table.AddRow("Number of Files", mdb.NumberOfFiles.ToString());
            table.AddRow("File Directory Start (sector)", mdb.FileDirectoryStart.ToString());
            table.AddRow("File Directory Length (sectors)", mdb.FileDirectoryLength.ToString());
            table.AddRow("Number of Allocation Blocks", mdb.NumberOfAllocationBlocks.ToString());
            table.AddRow("Allocation Block Size (bytes)", mdb.AllocationBlockSize.ToString());
            table.AddRow("Clump Size (bytes)", mdb.ClumpSize.ToString());
            table.AddRow("Allocation Block Start (sector)", mdb.AllocationBlockStart.ToString());
            table.AddRow("Next File Number", mdb.NextFileNumber.ToString());
            table.AddRow("Free Allocation Blocks", mdb.FreeAllocationBlocks.ToString());

            // Calculate derived information
            var totalSpace = (long)mdb.NumberOfAllocationBlocks * mdb.AllocationBlockSize;
            var freeSpace = (long)mdb.FreeAllocationBlocks * mdb.AllocationBlockSize;
            var usedSpace = totalSpace - freeSpace;

            table.AddRow("[dim]──────────────────[/]", "[dim]──────────────────[/]");
            table.AddRow("Total Space", FormatBytes(totalSpace));
            table.AddRow("Used Space", FormatBytes(usedSpace));
            table.AddRow("Free Space", FormatBytes(freeSpace));

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            volumeIndex++;
        }

        return Task.FromResult(0);
    }

    private static IEnumerable<MfsVolume>? GetSelectedVolumes(MfsDisk disk, int? volumeIndex)
    {
        if (volumeIndex.HasValue)
        {
            if (volumeIndex < 1 || volumeIndex > disk.Volumes.Count)
            {
                AnsiConsole.MarkupLine($"[red]Invalid volume index[/]: {volumeIndex}. Disk has {disk.Volumes.Count} volume(s).");
                return null;
            }
            return [disk.Volumes[volumeIndex.Value - 1]];
        }
        return disk.Volumes;
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]} ({bytes:N0} bytes)";
    }
}

sealed class ListSettings : InputSettings
{
    [CommandOption("-b|--brief")]
    [Description("Show only the summary table without detailed entry information.")]
    public bool Brief { get; init; }

    [CommandOption("-f|--filter")]
    [Description("Filter files by name pattern (supports * and ? wildcards).")]
    public string? Filter { get; init; }
}

sealed class ListCommand : AsyncCommand<ListSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ListSettings settings, CancellationToken cancellationToken)
    {
        var input = new FileInfo(settings.Input);
        if (!input.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Input file not found[/]: {input.FullName}");
            return Task.FromResult(-1);
        }

        using var stream = input.OpenRead();
        var disk = new MfsDisk(stream);

        AnsiConsole.MarkupLine($"[green]Found[/] {disk.Volumes.Count} volume(s) on disk.");
        AnsiConsole.WriteLine();

        var volumes = GetSelectedVolumes(disk, settings.VolumeIndex);
        if (volumes == null)
            return Task.FromResult(-1);

        Regex? filterRegex = null;
        if (!string.IsNullOrEmpty(settings.Filter))
        {
            var pattern = "^" + Regex.Escape(settings.Filter).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            filterRegex = new Regex(pattern, RegexOptions.IgnoreCase);
        }

        foreach (var volume in volumes)
        {
            var allEntries = volume.GetEntries().ToList();
            var entries = filterRegex != null 
                ? allEntries.Where(e => filterRegex.IsMatch(e.Name)).ToList() 
                : allEntries;
            
            var countInfo = filterRegex != null 
                ? $"{entries.Count} of {allEntries.Count} files" 
                : $"{entries.Count} files";
            AnsiConsole.MarkupLine($"[bold underline]Volume: {volume.MasterDirectoryBlock.VolumeName}[/] ({countInfo})");
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("#");
            table.AddColumn("Name");
            table.AddColumn("Type");
            table.AddColumn("Creator");
            table.AddColumn("Data Fork");
            table.AddColumn("Resource Fork");
            table.AddColumn("Created");
            table.AddColumn("Modified");
            table.AddColumn("Flags");
            table.Border = TableBorder.Rounded;

            int index = 1;
            foreach (var entry in entries)
            {
                var flags = new List<string>();
                if (entry.Flags.HasFlag(MfsFileDirectoryBlockFlags.Locked))
                    flags.Add("Locked");

                table.AddRow(
                    index.ToString(),
                    Markup.Escape(entry.Name),
                    Markup.Escape(entry.FileType),
                    Markup.Escape(entry.Creator),
                    FormatForkSize(entry.DataForkSize, entry.DataForkAllocatedSize, entry.DataForkAllocationBlock),
                    FormatForkSize(entry.ResourceForkSize, entry.ResourceForkAllocatedSize, entry.ResourceForkAllocationBlock),
                    entry.CreationDate.ToString("yyyy-MM-dd HH:mm"),
                    entry.LastModificationDate.ToString("yyyy-MM-dd HH:mm"),
                    flags.Count > 0 ? string.Join(", ", flags) : "[dim]-[/]"
                );
                index++;
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            if (!settings.Brief)
            {
                // Show detailed entry info
                AnsiConsole.MarkupLine("[bold]Detailed Entry Information:[/]");
                AnsiConsole.WriteLine();

                foreach (var entry in entries)
                {
                    var detailTable = new Table();
                    detailTable.AddColumn("Field");
                    detailTable.AddColumn("Value");
                    detailTable.Border = TableBorder.Simple;
                    detailTable.Title = new TableTitle($"[bold]{Markup.Escape(entry.Name)}[/]");

                    detailTable.AddRow("File Number", entry.FileNumber.ToString());
                    detailTable.AddRow("Flags", entry.Flags.ToString());
                    detailTable.AddRow("Version", entry.Version.ToString());
                    detailTable.AddRow("File Type", Markup.Escape(entry.FileType));
                    detailTable.AddRow("Creator", Markup.Escape(entry.Creator));
                    detailTable.AddRow("Finder Flags", $"0x{entry.FinderFlags:X4}");
                    detailTable.AddRow("Parent Location", $"({entry.ParentLocationX}, {entry.ParentLocationY})");
                    detailTable.AddRow("Folder Number", entry.FolderNumber.ToString());
                    detailTable.AddRow("[dim]── Data Fork ──[/]", "");
                    detailTable.AddRow("  First Block", entry.DataForkAllocationBlock.ToString());
                    detailTable.AddRow("  Logical Size", $"{entry.DataForkSize:N0} bytes");
                    detailTable.AddRow("  Physical Size", $"{entry.DataForkAllocatedSize:N0} bytes");
                    detailTable.AddRow("[dim]── Resource Fork ──[/]", "");
                    detailTable.AddRow("  First Block", entry.ResourceForkAllocationBlock.ToString());
                    detailTable.AddRow("  Logical Size", $"{entry.ResourceForkSize:N0} bytes");
                    detailTable.AddRow("  Physical Size", $"{entry.ResourceForkAllocatedSize:N0} bytes");
                    detailTable.AddRow("[dim]── Timestamps ──[/]", "");
                    detailTable.AddRow("  Created", entry.CreationDate.ToString("yyyy-MM-dd HH:mm:ss"));
                    detailTable.AddRow("  Modified", entry.LastModificationDate.ToString("yyyy-MM-dd HH:mm:ss"));

                    AnsiConsole.Write(detailTable);
                    AnsiConsole.WriteLine();
                }
            }
        }

        return Task.FromResult(0);
    }

    private static string FormatForkSize(uint size, uint allocatedSize, ushort firstBlock)
    {
        if (size == 0 && firstBlock == 0)
            return "[dim]-[/]";
        return $"{size:N0} bytes (alloc: {allocatedSize:N0}, block: {firstBlock})";
    }

    private static IEnumerable<MfsVolume>? GetSelectedVolumes(MfsDisk disk, int? volumeIndex)
    {
        if (volumeIndex.HasValue)
        {
            if (volumeIndex < 1 || volumeIndex > disk.Volumes.Count)
            {
                AnsiConsole.MarkupLine($"[red]Invalid volume index[/]: {volumeIndex}. Disk has {disk.Volumes.Count} volume(s).");
                return null;
            }
            return [disk.Volumes[volumeIndex.Value - 1]];
        }
        return disk.Volumes;
    }
}

sealed class MapSettings : InputSettings
{
    [CommandOption("--used-only")]
    [Description("Show only used allocation blocks.")]
    public bool UsedOnly { get; init; }

    [CommandOption("--free-only")]
    [Description("Show only free allocation blocks.")]
    public bool FreeOnly { get; init; }
}

sealed class MapCommand : AsyncCommand<MapSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, MapSettings settings, CancellationToken cancellationToken)
    {
        var input = new FileInfo(settings.Input);
        if (!input.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Input file not found[/]: {input.FullName}");
            return Task.FromResult(-1);
        }

        using var stream = input.OpenRead();
        var disk = new MfsDisk(stream);

        AnsiConsole.MarkupLine($"[green]Found[/] {disk.Volumes.Count} volume(s) on disk.");
        AnsiConsole.WriteLine();

        var volumes = GetSelectedVolumes(disk, settings.VolumeIndex);
        if (volumes == null)
            return Task.FromResult(-1);

        foreach (var volume in volumes)
        {
            var mdb = volume.MasterDirectoryBlock;
            var map = volume.AllocationBlockMap;

            AnsiConsole.MarkupLine($"[bold underline]Volume: {mdb.VolumeName}[/]");
            AnsiConsole.MarkupLine($"Allocation blocks: {mdb.NumberOfAllocationBlocks} (block size: {mdb.AllocationBlockSize} bytes)");
            AnsiConsole.MarkupLine($"Free blocks: {mdb.FreeAllocationBlocks}");
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("Block");
            table.AddColumn("Next");
            table.AddColumn("Status");
            table.Border = TableBorder.Simple;

            // Block numbering starts at 2
            for (int i = 2; i < map.Entries.Length; i++)
            {
                ushort next = map.Entries[i];
                string status;
                string nextStr;

                if (next == 0)
                {
                    status = "[green]Free[/]";
                    nextStr = "-";
                    if (settings.UsedOnly) continue;
                }
                else if (next == 1)
                {
                    status = "[blue]End of file[/]";
                    nextStr = "EOF";
                    if (settings.FreeOnly) continue;
                }
                else
                {
                    status = "[yellow]In use[/]";
                    nextStr = next.ToString();
                    if (settings.FreeOnly) continue;
                }

                table.AddRow(i.ToString(), nextStr, status);
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            // Show a visual block map
            AnsiConsole.MarkupLine("[bold]Visual Block Map:[/] ([green]■[/]=free, [yellow]■[/]=used, [blue]■[/]=EOF)");
            var visualMap = new System.Text.StringBuilder();
            int blocksPerRow = 64;
            for (int i = 2; i < map.Entries.Length; i++)
            {
                if ((i - 2) % blocksPerRow == 0 && i > 2)
                {
                    AnsiConsole.MarkupLine(visualMap.ToString());
                    visualMap.Clear();
                }
                
                ushort next = map.Entries[i];
                if (next == 0)
                    visualMap.Append("[green]■[/]");
                else if (next == 1)
                    visualMap.Append("[blue]■[/]");
                else
                    visualMap.Append("[yellow]■[/]");
            }
            if (visualMap.Length > 0)
                AnsiConsole.MarkupLine(visualMap.ToString());
            AnsiConsole.WriteLine();
        }

        return Task.FromResult(0);
    }

    private static IEnumerable<MfsVolume>? GetSelectedVolumes(MfsDisk disk, int? volumeIndex)
    {
        if (volumeIndex.HasValue)
        {
            if (volumeIndex < 1 || volumeIndex > disk.Volumes.Count)
            {
                AnsiConsole.MarkupLine($"[red]Invalid volume index[/]: {volumeIndex}. Disk has {disk.Volumes.Count} volume(s).");
                return null;
            }
            return [disk.Volumes[volumeIndex.Value - 1]];
        }
        return disk.Volumes;
    }
}

sealed class HexDumpSettings : InputSettings
{
    [CommandArgument(1, "<filename>")]
    [Description("Name of the file to hex dump.")]
    public required string FileName { get; init; }

    [CommandOption("--fork")]
    [Description("Which fork to dump: 'data' (default), 'resource', or 'both'.")]
    [DefaultValue("data")]
    public string Fork { get; init; } = "data";

    [CommandOption("-n|--bytes")]
    [Description("Maximum number of bytes to dump (default: 256).")]
    [DefaultValue(256)]
    public int MaxBytes { get; init; } = 256;

    [CommandOption("--offset")]
    [Description("Offset in bytes to start dumping from.")]
    [DefaultValue(0)]
    public int Offset { get; init; } = 0;
}

sealed class HexDumpCommand : AsyncCommand<HexDumpSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, HexDumpSettings settings, CancellationToken cancellationToken)
    {
        var input = new FileInfo(settings.Input);
        if (!input.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Input file not found[/]: {input.FullName}");
            return Task.FromResult(-1);
        }

        using var stream = input.OpenRead();
        var disk = new MfsDisk(stream);

        MfsVolume? targetVolume = null;
        MfsFileDirectoryBlock? targetEntry = null;

        var volumes = settings.VolumeIndex.HasValue 
            ? [disk.Volumes[settings.VolumeIndex.Value - 1]] 
            : disk.Volumes;

        foreach (var volume in volumes)
        {
            var entry = volume.GetEntries().FirstOrDefault(e => 
                e.Name.Equals(settings.FileName, StringComparison.OrdinalIgnoreCase));
            if (entry.Name != null)
            {
                targetVolume = volume;
                targetEntry = entry;
                break;
            }
        }

        if (targetVolume == null || targetEntry == null)
        {
            AnsiConsole.MarkupLine($"[red]File not found[/]: {settings.FileName}");
            return Task.FromResult(-1);
        }

        var entry2 = targetEntry.Value;
        AnsiConsole.MarkupLine($"[bold]File:[/] {entry2.Name}");
        AnsiConsole.MarkupLine($"[bold]Type:[/] {entry2.FileType} / {entry2.Creator}");
        AnsiConsole.WriteLine();

        bool dumpData = settings.Fork is "data" or "both";
        bool dumpResource = settings.Fork is "resource" or "both";

        if (dumpData && entry2.DataForkSize > 0)
        {
            AnsiConsole.MarkupLine($"[bold underline]Data Fork[/] ({entry2.DataForkSize:N0} bytes)");
            var data = targetVolume.GetDataForkData(entry2);
            DumpHex(data, settings.Offset, settings.MaxBytes);
            AnsiConsole.WriteLine();
        }
        else if (dumpData && entry2.DataForkSize == 0)
        {
            AnsiConsole.MarkupLine("[dim]Data fork is empty.[/]");
            AnsiConsole.WriteLine();
        }

        if (dumpResource && entry2.ResourceForkSize > 0)
        {
            AnsiConsole.MarkupLine($"[bold underline]Resource Fork[/] ({entry2.ResourceForkSize:N0} bytes)");
            var data = targetVolume.GetResourceForkData(entry2);
            DumpHex(data, settings.Offset, settings.MaxBytes);
            AnsiConsole.WriteLine();
        }
        else if (dumpResource && entry2.ResourceForkSize == 0)
        {
            AnsiConsole.MarkupLine("[dim]Resource fork is empty.[/]");
            AnsiConsole.WriteLine();
        }

        return Task.FromResult(0);
    }

    private static void DumpHex(byte[] data, int offset, int maxBytes)
    {
        if (offset >= data.Length)
        {
            AnsiConsole.MarkupLine("[yellow]Offset beyond end of data.[/]");
            return;
        }

        int endOffset = Math.Min(offset + maxBytes, data.Length);
        int bytesPerLine = 16;

        for (int i = offset; i < endOffset; i += bytesPerLine)
        {
            var hex = new System.Text.StringBuilder();
            var ascii = new System.Text.StringBuilder();

            hex.Append($"[dim]{i:X8}[/]  ");

            for (int j = 0; j < bytesPerLine; j++)
            {
                if (i + j < endOffset)
                {
                    byte b = data[i + j];
                    hex.Append($"{b:X2} ");
                    ascii.Append(b is >= 32 and < 127 ? (char)b : '.');
                }
                else
                {
                    hex.Append("   ");
                }

                if (j == 7) hex.Append(' ');
            }

            AnsiConsole.MarkupLine($"{hex} [dim]|[/]{Markup.Escape(ascii.ToString())}[dim]|[/]");
        }

        if (endOffset < data.Length)
        {
            AnsiConsole.MarkupLine($"[dim]... ({data.Length - endOffset:N0} more bytes)[/]");
        }
    }
}

sealed class ExtractSettings : CommandSettings
{
    [CommandArgument(0, "<input>")]
    [Description("Path to the MFS disk image file.")]
    public required string Input { get; init; }

    [CommandOption("-o|--output")]
    [Description("Output directory for extracted files.")]
    public string? Output { get; init; }

    [CommandOption("-v|--volume")]
    [Description("Volume index (1-based) to extract from. Defaults to all volumes.")]
    public int? VolumeIndex { get; init; }

    [CommandOption("-f|--filter")]
    [Description("Filter files by name pattern (supports * and ? wildcards).")]
    public string? Filter { get; init; }

    [CommandOption("--data-only")]
    [Description("Extract only data forks.")]
    public bool DataOnly { get; init; }

    [CommandOption("--resource-only")]
    [Description("Extract only resource forks.")]
    public bool ResourceOnly { get; init; }

    [CommandOption("--preserve-dates")]
    [Description("Preserve original creation and modification dates.")]
    [DefaultValue(true)]
    public bool PreserveDates { get; init; } = true;

    [CommandOption("--dry-run")]
    [Description("Show what would be extracted without actually extracting.")]
    public bool DryRun { get; init; }
}

sealed class ExtractCommand : AsyncCommand<ExtractSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ExtractSettings settings, CancellationToken cancellationToken)
    {
        var input = new FileInfo(settings.Input);
        if (!input.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Input file not found[/]: {input.FullName}");
            return -1;
        }

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine("[yellow]Dry run mode - no files will be extracted.[/]");
        }

        var outputPath = settings.Output ?? Path.GetFileNameWithoutExtension(input.Name);
        var outputDir = new DirectoryInfo(outputPath);
        if (!outputDir.Exists && !settings.DryRun)
        {
            outputDir.Create();
        }

        await using var stream = input.OpenRead();
        var disk = new MfsDisk(stream);

        AnsiConsole.MarkupLine($"[green]Found[/] {disk.Volumes.Count} volume(s) on disk.");

        var volumes = GetSelectedVolumes(disk, settings.VolumeIndex);
        if (volumes == null)
            return -1;

        Regex? filterRegex = null;
        if (!string.IsNullOrEmpty(settings.Filter))
        {
            var pattern = "^" + Regex.Escape(settings.Filter).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            filterRegex = new Regex(pattern, RegexOptions.IgnoreCase);
        }

        int totalFiles = 0;
        long totalBytes = 0;

        foreach (var volume in volumes)
        {
            var allEntries = volume.GetEntries().ToList();
            var entries = filterRegex != null 
                ? allEntries.Where(e => filterRegex.IsMatch(e.Name)).ToList() 
                : allEntries;

            var countInfo = filterRegex != null 
                ? $"{entries.Count} of {allEntries.Count}" 
                : entries.Count.ToString();
            AnsiConsole.MarkupLine($"[green]Processing[/] {countInfo} items in volume '[bold]{volume.MasterDirectoryBlock.VolumeName}[/]'.");

            foreach (var entry in entries)
            {
                var safeName = SanitizeName(entry.Name);
                var basePath = Path.Combine(outputDir.FullName, safeName);

                bool extractData = !settings.ResourceOnly && entry.DataForkSize != 0;
                bool extractResource = !settings.DataOnly && entry.ResourceForkSize != 0;

                if (!extractData && !extractResource)
                {
                    AnsiConsole.MarkupLine($"  [dim]Skipping[/] {Markup.Escape(entry.Name)} (no selected forks)");
                    continue;
                }

                if (extractData)
                {
                    var dataPath = basePath + ".data";
                    if (settings.DryRun)
                    {
                        AnsiConsole.MarkupLine($"  [cyan]Would extract[/] data fork: {Markup.Escape(entry.Name)} → {Path.GetFileName(dataPath)} ({entry.DataForkSize:N0} bytes)");
                    }
                    else
                    {
                        await using var outputStream = File.Create(dataPath);
                        var bytes = volume.GetFileData(entry, outputStream, MfsForkType.DataFork);
                        AnsiConsole.MarkupLine($"  [green]✓[/] Data fork: {Markup.Escape(entry.Name)} → {Path.GetFileName(dataPath)} ({bytes:N0} bytes)");
                        if (settings.PreserveDates)
                            TrySetTimestamps(dataPath, entry);
                        totalBytes += bytes;
                    }
                    totalFiles++;
                }

                if (extractResource)
                {
                    var resPath = basePath + ".res";
                    if (settings.DryRun)
                    {
                        AnsiConsole.MarkupLine($"  [cyan]Would extract[/] resource fork: {Markup.Escape(entry.Name)} → {Path.GetFileName(resPath)} ({entry.ResourceForkSize:N0} bytes)");
                    }
                    else
                    {
                        await using var outputStream = File.Create(resPath);
                        var bytes = volume.GetFileData(entry, outputStream, MfsForkType.ResourceFork);
                        AnsiConsole.MarkupLine($"  [green]✓[/] Resource fork: {Markup.Escape(entry.Name)} → {Path.GetFileName(resPath)} ({bytes:N0} bytes)");
                        if (settings.PreserveDates)
                            TrySetTimestamps(resPath, entry);
                        totalBytes += bytes;
                    }
                    totalFiles++;
                }
            }
        }

        AnsiConsole.WriteLine();
        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine($"[yellow]Dry run complete[/]: Would extract {totalFiles} file(s).");
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]Extraction complete[/]: {totalFiles} file(s), {totalBytes:N0} bytes → {outputDir.FullName}");
        }
        return 0;
    }

    private static IEnumerable<MfsVolume>? GetSelectedVolumes(MfsDisk disk, int? volumeIndex)
    {
        if (volumeIndex.HasValue)
        {
            if (volumeIndex < 1 || volumeIndex > disk.Volumes.Count)
            {
                AnsiConsole.MarkupLine($"[red]Invalid volume index[/]: {volumeIndex}. Disk has {disk.Volumes.Count} volume(s).");
                return null;
            }
            return [disk.Volumes[volumeIndex.Value - 1]];
        }
        return disk.Volumes;
    }

    private static void TrySetTimestamps(string path, MfsFileDirectoryBlock entry)
    {
        try
        {
            File.SetLastWriteTime(path, entry.LastModificationDate);
            File.SetCreationTime(path, entry.CreationDate);
        }
        catch
        {
            // Ignore timestamp errors
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
