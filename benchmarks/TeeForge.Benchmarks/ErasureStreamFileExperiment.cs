using System.Diagnostics;
using System.Globalization;
using System.Text;
using TeeForge.ErasureCoding;
using TeeForge.RandomAccess;

namespace TeeForge.Benchmarks;

internal static class ErasureStreamFileExperiment
{
    private const int DataShardCount = 4;
    private const int ParityShardCount = 2;
    private const int RandomRequestSize = 4096;
    private static readonly int[] BlockSizes =
        [4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576];

    internal static Task RunAsync(string[] args) => RunAsync(args, useMemory: false);

    internal static Task RunMemoryAsync(string[] args) => RunAsync(args, useMemory: true);

    private static async Task RunAsync(string[] args, bool useMemory)
    {
        int dataMiB = ReadPositiveOption(args, "--data-mib", 64);
        int randomOperations = ReadPositiveOption(args, "--random-operations", 256);
        int? selectedBlockSize = ReadOption(args, "--block-size") is null
            ? null
            : ReadPositiveOption(args, "--block-size", 0);
        if (selectedBlockSize is not null && !BlockSizes.Contains(selectedBlockSize.Value))
        {
            throw new ArgumentException("--block-size must be a power of two from 4096 through 1048576.");
        }

        int[] blockSizes = selectedBlockSize is null ? BlockSizes : [selectedBlockSize.Value];
        string outputDirectory = ReadOption(args, "--output") ??
            Path.Combine("benchmarks", "TeeForge.Benchmarks", "Experiments");
        Directory.CreateDirectory(outputDirectory);
        string temporaryRoot = Path.Combine(Path.GetTempPath(), $"teeforge-erasure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            string backingName = useMemory ? "RandomAccessMemoryStream" : "local-file";
            string outputStem = useMemory
                ? "2026-08-26-erasure-stream-memory-block-size"
                : "2026-08-26-erasure-stream-block-size";
            if (selectedBlockSize is not null)
            {
                outputStem += $"-{FormatBytes(selectedBlockSize.Value).ToLowerInvariant()}";
            }

            Console.WriteLine($"4+2 {backingName} experiment: {dataMiB} MiB logical, {randomOperations} random 4 KiB operations");
            var results = new List<Result>(blockSizes.Length);
            foreach (int blockSize in blockSizes)
            {
                Console.WriteLine($"Measuring {FormatBytes(blockSize)} blocks...");
                string caseDirectory = Path.Combine(temporaryRoot, blockSize.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(caseDirectory);
                results.Add(await MeasureCaseAsync(
                    caseDirectory,
                    blockSize,
                    dataMiB,
                    randomOperations,
                    useMemory).ConfigureAwait(false));
                if (useMemory)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                }
            }

            string csvPath = Path.Combine(outputDirectory, $"{outputStem}.csv");
            await File.WriteAllTextAsync(csvPath, CreateCsv(results)).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, $"{outputStem}-performance.svg"),
                CreatePerformanceChart(results, backingName)).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, $"{outputStem}-resources.svg"),
                CreateResourceChart(results, backingName)).ConfigureAwait(false);
            Console.WriteLine($"Wrote {csvPath}");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static async Task<Result> MeasureCaseAsync(
        string directory,
        int blockSize,
        int dataMiB,
        int randomOperations,
        bool useMemory)
    {
        long logicalLength = checked((long)dataMiB * 1024 * 1024);
        using var storage = new ExperimentStorage(directory, useMemory);
        byte[] transfer = new byte[1024 * 1024];
        new Random(0x544646).NextBytes(transfer);
        var options = new ErasureStreamOptions(
            leaveOpen: useMemory,
            maximumCacheBytes: 64L * 1024 * 1024);

        Measurement sequentialWrite = await MeasureAsync(async () =>
        {
            await using ErasureStream stream = ErasureStream.Create(
                storage.Open(), DataShardCount, ParityShardCount, logicalLength, blockSize, options);
            long remaining = logicalLength;
            while (remaining > 0)
            {
                int count = (int)Math.Min(transfer.Length, remaining);
                await stream.WriteAsync(transfer.AsMemory(0, count)).ConfigureAwait(false);
                remaining -= count;
            }

            await stream.CompleteAsync().ConfigureAwait(false);
        }, logicalLength / (1024d * 1024d)).ConfigureAwait(false);

        Measurement sequentialRead = await MeasureAsync(async () =>
        {
            await using ErasureStream stream = ErasureStream.Open(storage.Open(), options);
            long remaining = logicalLength;
            long checksum = 0;
            while (remaining > 0)
            {
                int count = (int)Math.Min(transfer.Length, remaining);
                int read = await stream.ReadAsync(transfer.AsMemory(0, count)).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                checksum += transfer[0];
                remaining -= read;
            }

            GC.KeepAlive(checksum);
        }, logicalLength / (1024d * 1024d)).ConfigureAwait(false);

        long[] offsets = CreateRandomOffsets(logicalLength, randomOperations);
        var request = new byte[RandomRequestSize];
        Measurement randomRead = await MeasureAsync(async () =>
        {
            await using ErasureStream stream = ErasureStream.Open(storage.Open(), options);
            long checksum = 0;
            foreach (long offset in offsets)
            {
                await stream.ReadAtAsync(request, offset).ConfigureAwait(false);
                checksum += request[0];
            }

            GC.KeepAlive(checksum);
        }, randomOperations).ConfigureAwait(false);

        new Random(0x524d57).NextBytes(request);
        Measurement randomWrite = await MeasureAsync(async () =>
        {
            await using ErasureStream stream = ErasureStream.Open(storage.Open(), options);
            foreach (long offset in offsets)
            {
                await stream.WriteAtAsync(request, offset).ConfigureAwait(false);
            }

            await stream.FlushAsync().ConfigureAwait(false);
        }, randomOperations).ConfigureAwait(false);

        return new Result(blockSize, sequentialWrite, sequentialRead, randomRead, randomWrite);
    }

    private static FileStream[] OpenFiles(IEnumerable<string> paths) => paths.Select(path => new FileStream(
        path,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.Read,
        bufferSize: 1,
        FileOptions.Asynchronous | FileOptions.RandomAccess)).ToArray();

    private static long[] CreateRandomOffsets(long logicalLength, int operations)
    {
        var random = new Random(0x52414e44);
        var result = new long[operations];
        long slots = (logicalLength - RandomRequestSize) / RandomRequestSize;
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = random.NextInt64(slots + 1) * RandomRequestSize;
        }

        return result;
    }

    private static async Task<Measurement> MeasureAsync(Func<Task> action, double units)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        long workingSetBefore = process.WorkingSet64;
        long peakWorkingSet = workingSetBefore;
        using var stopSampling = new CancellationTokenSource();
        Task sampler = Task.Run(async () =>
        {
            while (!stopSampling.IsCancellationRequested)
            {
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                try
                {
                    await Task.Delay(10, stopSampling.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });

        var stopwatch = Stopwatch.StartNew();
        await action().ConfigureAwait(false);
        stopwatch.Stop();
        stopSampling.Cancel();
        await sampler.ConfigureAwait(false);
        process.Refresh();
        peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        double cpuPercent = (process.TotalProcessorTime - cpuBefore).TotalSeconds /
            stopwatch.Elapsed.TotalSeconds / Environment.ProcessorCount * 100d;
        return new Measurement(
            units / stopwatch.Elapsed.TotalSeconds,
            cpuPercent,
            Math.Max(0, peakWorkingSet - workingSetBefore) / (1024d * 1024d),
            (GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore) / (1024d * 1024d));
    }

    private static string CreateCsv(IEnumerable<Result> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("BlockSizeBytes,SequentialWriteMiBps,SequentialWriteCpuPercent,SequentialWritePeakWorkingSetIncreaseMiB,SequentialWriteAllocatedMiB,SequentialReadMiBps,SequentialReadCpuPercent,SequentialReadPeakWorkingSetIncreaseMiB,SequentialReadAllocatedMiB,RandomReadIops,RandomReadCpuPercent,RandomReadPeakWorkingSetIncreaseMiB,RandomReadAllocatedMiB,RandomWriteIops,RandomWriteCpuPercent,RandomWritePeakWorkingSetIncreaseMiB,RandomWriteAllocatedMiB");
        foreach (Result result in results)
        {
            builder.Append(result.BlockSize).Append(',');
            AppendMeasurement(builder, result.SequentialWrite);
            AppendMeasurement(builder, result.SequentialRead);
            AppendMeasurement(builder, result.RandomRead);
            AppendMeasurement(builder, result.RandomWrite, final: true);
        }

        return builder.ToString();
    }

    private static void AppendMeasurement(StringBuilder builder, Measurement measurement, bool final = false)
    {
        builder.Append(measurement.Rate.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
            .Append(measurement.CpuPercent.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
            .Append(measurement.PeakWorkingSetIncreaseMiB.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
            .Append(measurement.AllocatedMiB.ToString("F3", CultureInfo.InvariantCulture))
            .Append(final ? '\n' : ',');
    }

    private static string CreatePerformanceChart(IReadOnlyList<Result> results, string backingName) => CreateChart(
        $"ErasureStream 4+2 {backingName} performance",
        results,
        [
            new Series("Sequential write MiB/s", result => result.SequentialWrite.Rate, "#2563eb"),
            new Series("Sequential read MiB/s", result => result.SequentialRead.Rate, "#16a34a")
        ],
        [
            new Series("Random read IOPS", result => result.RandomRead.Rate, "#7c3aed"),
            new Series("Random write IOPS", result => result.RandomWrite.Rate, "#dc2626")
        ]);

    private static string CreateResourceChart(IReadOnlyList<Result> results, string backingName) => CreateChart(
        $"ErasureStream 4+2 {backingName} process resources",
        results,
        [
            new Series("Sequential write CPU %", result => result.SequentialWrite.CpuPercent, "#2563eb"),
            new Series("Sequential read CPU %", result => result.SequentialRead.CpuPercent, "#16a34a"),
            new Series("Random read CPU %", result => result.RandomRead.CpuPercent, "#7c3aed"),
            new Series("Random write CPU %", result => result.RandomWrite.CpuPercent, "#dc2626")
        ],
        [
            new Series("Write working-set increase MiB", result => result.SequentialWrite.PeakWorkingSetIncreaseMiB, "#2563eb"),
            new Series("Read working-set increase MiB", result => result.SequentialRead.PeakWorkingSetIncreaseMiB, "#16a34a"),
            new Series("Random read increase MiB", result => result.RandomRead.PeakWorkingSetIncreaseMiB, "#7c3aed"),
            new Series("Random write increase MiB", result => result.RandomWrite.PeakWorkingSetIncreaseMiB, "#dc2626")
        ]);

    private static string CreateChart(
        string title,
        IReadOnlyList<Result> results,
        IReadOnlyList<Series> top,
        IReadOnlyList<Series> bottom)
    {
        var svg = new StringBuilder();
        svg.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1200\" height=\"760\" viewBox=\"0 0 1200 760\">")
            .AppendLine("<rect width=\"1200\" height=\"760\" fill=\"#ffffff\"/>")
            .Append("<text x=\"600\" y=\"34\" text-anchor=\"middle\" font-family=\"sans-serif\" font-size=\"22\" font-weight=\"bold\">")
            .Append(title).AppendLine("</text>");
        DrawPanel(svg, results, top, 70);
        DrawPanel(svg, results, bottom, 405);
        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    private static void DrawPanel(
        StringBuilder svg,
        IReadOnlyList<Result> results,
        IReadOnlyList<Series> series,
        int top)
    {
        const int left = 85;
        const int width = 1060;
        const int height = 245;
        double maximum = series.SelectMany(item => results.Select(item.Selector)).Max();
        maximum = maximum <= 0 ? 1 : maximum * 1.1;
        svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{left}\" y1=\"{top}\" x2=\"{left}\" y2=\"{top + height}\" stroke=\"#64748b\"/>")
            .Append(CultureInfo.InvariantCulture, $"<line x1=\"{left}\" y1=\"{top + height}\" x2=\"{left + width}\" y2=\"{top + height}\" stroke=\"#64748b\"/>");
        for (int tick = 0; tick <= 4; tick++)
        {
            double value = maximum * tick / 4;
            double y = top + height - height * tick / 4d;
            svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{left}\" y1=\"{y:F1}\" x2=\"{left + width}\" y2=\"{y:F1}\" stroke=\"#e2e8f0\"/>")
                .Append(CultureInfo.InvariantCulture, $"<text x=\"{left - 8}\" y=\"{y + 4:F1}\" text-anchor=\"end\" font-family=\"sans-serif\" font-size=\"11\">{value:F0}</text>");
        }

        for (int index = 0; index < results.Count; index++)
        {
            double x = results.Count == 1
                ? left + width / 2d
                : left + width * index / (double)(results.Count - 1);
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{x:F1}\" y=\"{top + height + 22}\" text-anchor=\"middle\" font-family=\"sans-serif\" font-size=\"11\">{FormatBytes(results[index].BlockSize)}</text>");
        }

        int legendX = left;
        foreach (Series item in series)
        {
            string points = string.Join(' ', results.Select((result, index) =>
            {
                double x = results.Count == 1
                    ? left + width / 2d
                    : left + width * index / (double)(results.Count - 1);
                double y = top + height - height * item.Selector(result) / maximum;
                return string.Create(CultureInfo.InvariantCulture, $"{x:F1},{y:F1}");
            }));
            svg.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{points}\" fill=\"none\" stroke=\"{item.Color}\" stroke-width=\"3\"/>")
                .Append(CultureInfo.InvariantCulture, $"<line x1=\"{legendX}\" y1=\"{top - 15}\" x2=\"{legendX + 22}\" y2=\"{top - 15}\" stroke=\"{item.Color}\" stroke-width=\"3\"/>")
                .Append(CultureInfo.InvariantCulture, $"<text x=\"{legendX + 28}\" y=\"{top - 11}\" font-family=\"sans-serif\" font-size=\"12\">{item.Name}</text>");
            legendX += 250;
        }
    }

    private static int ReadPositiveOption(string[] args, string name, int fallback)
    {
        string? value = ReadOption(args, name);
        if (value is null)
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{name} requires a positive integer.");
        }

        return parsed;
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string FormatBytes(int bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024}K"
        : $"{bytes / (1024 * 1024)}M";

    private sealed record Measurement(
        double Rate,
        double CpuPercent,
        double PeakWorkingSetIncreaseMiB,
        double AllocatedMiB);

    private sealed record Result(
        int BlockSize,
        Measurement SequentialWrite,
        Measurement SequentialRead,
        Measurement RandomRead,
        Measurement RandomWrite);

    private sealed record Series(string Name, Func<Result, double> Selector, string Color);

    private sealed class ExperimentStorage : IDisposable
    {
        private readonly string[] _paths;
        private readonly RandomAccessMemoryStream[]? _memoryMembers;

        internal ExperimentStorage(string directory, bool useMemory)
        {
            _paths = Enumerable.Range(0, DataShardCount + ParityShardCount)
                .Select(index => Path.Combine(directory, $"member-{index}.bin"))
                .ToArray();
            if (useMemory)
            {
                _memoryMembers = Enumerable.Range(0, DataShardCount + ParityShardCount)
                    .Select(static _ => new RandomAccessMemoryStream())
                    .ToArray();
            }
        }

        internal IReadOnlyList<Stream> Open() => _memoryMembers is null
            ? OpenFiles(_paths)
            : _memoryMembers;

        public void Dispose()
        {
            if (_memoryMembers is null)
            {
                return;
            }

            foreach (RandomAccessMemoryStream member in _memoryMembers)
            {
                member.Dispose();
            }
        }
    }
}
