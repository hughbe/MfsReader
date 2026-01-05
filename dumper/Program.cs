using Spectre.Console;
using Spectre.Console.Cli;
using MfsReader;

public sealed class Program
{
    public static int Main(string[] args)
    {
        var app = new CommandApp<ExtractCommand>();
        app.Configure(config =>
        {
            config.SetApplicationName("mfs-dumper");
            config.ValidateExamples();
        });

        return app.Run(args);
    }
}

sealed class ExtractSettings : CommandSettings
{
    [CommandArgument(0, "<input>")]
    public required string Input { get; init; }

    [CommandOption("-o|--output")]
    public string? Output { get; init; }

    [CommandOption("--data-only")]
    public bool DataOnly { get; init; }

    [CommandOption("--resource-only")]
    public bool ResourceOnly { get; init; }
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

        var outputPath = settings.Output ?? Path.GetFileNameWithoutExtension(input.Name);
        var outputDir = new DirectoryInfo(outputPath);
        if (!outputDir.Exists)
        {
            outputDir.Create();
        }

        await using var stream = input.OpenRead();
        var volume = new MFSVolume(stream);

        var entries = volume.GetEntries().ToList();
        AnsiConsole.MarkupLine($"[green]Found[/] {entries.Count} items in volume.");

        foreach (var entry in entries)
        {
            var safeName = SanitizeName(entry.Name);
            var basePath = Path.Combine(outputDir.FullName, safeName);

            bool extractData = !settings.ResourceOnly && entry.DataForkSize != 0;
            bool extractResource = !settings.DataOnly && entry.ResourceForkSize != 0;

            if (!extractData && !extractResource)
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping[/] {entry.Name} (no selected forks).");
                continue;
            }

            if (extractData)
            {
                var dataPath = basePath + ".data";
                await using var outputStream = File.Create(dataPath);
                var bytes = volume.GetFileData(entry, outputStream, resourceFork: false);
                AnsiConsole.MarkupLine($"Wrote data fork: {Path.GetFileName(dataPath)} ({bytes} bytes)");
                TrySetTimestamps(dataPath, entry);
            }

            if (extractResource)
            {
                var resPath = basePath + ".res";
                await using var outputStream = File.Create(resPath);
                var bytes = volume.GetFileData(entry, outputStream, resourceFork: true);
                AnsiConsole.MarkupLine($"Wrote resource fork: {Path.GetFileName(resPath)} ({bytes} bytes)");
                TrySetTimestamps(resPath, entry);
            }
        }

        AnsiConsole.MarkupLine($"[green]Extraction complete[/]: {outputDir.FullName}");
        return 0;
    }

    private static void TrySetTimestamps(string path, MFSFileDirectoryBlock entry)
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
