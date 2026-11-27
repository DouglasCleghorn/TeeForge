using System.Globalization;
using TeeForge.Quickstart;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
if (args.Length >= 2 && args[0] is "remote-zip" or "remote-parquet")
{
    using var counter = new RangeRequestCounter();
    using var client = new HttpClient(counter);
    if (args is ["remote-zip", var address, var entryName])
    {
        await using Stream output = Console.OpenStandardOutput();
        await RemoteZipExample.RunRemoteAsync(client, new Uri(address), entryName, output, timeout.Token);
    }
    else if (args is ["remote-parquet", var url, var columnName, var index] &&
        int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out int rowGroup))
    {
        await RemoteParquetExample.RunRemoteAsync(client, new Uri(url), columnName, rowGroup, timeout.Token);
    }
    else
    {
        Console.Error.WriteLine("Use remote-zip URL ENTRY or remote-parquet URL DOUBLE_COLUMN ROW_GROUP.");
        return 1;
    }

    counter.PrintTotals();
    return 0;
}

(string Name, Func<CancellationToken, Task> Run)[] examples =
[
    ("copy", CopyExample.RunAsync),
    ("hash", HashExample.RunAsync),
    ("replicate", ReplicationExample.RunAsync),
    ("broadcast", BroadcastExample.RunAsync),
    ("random-access", RandomAccessExample.RunAsync),
    ("remote-zip", RemoteZipExample.RunAsync),
    ("remote-parquet", RemoteParquetExample.RunAsync),
];

if (args.Length > 1 || (args.Length == 1 && !examples.Any(example => example.Name == args[0])))
{
    Console.Error.WriteLine("Choose copy, hash, replicate, broadcast, random-access, remote-zip, or remote-parquet. Omit arguments to run all examples.");
    return 1;
}

foreach ((string name, Func<CancellationToken, Task> run) in examples)
{
    if (args.Length == 0 || args[0] == name)
    {
        await run(timeout.Token);
        Console.WriteLine($"PASS: {name}");
    }
}

return 0;
