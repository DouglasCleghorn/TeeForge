using TeeForge.Quickstart;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
(string Name, Func<CancellationToken, Task> Run)[] examples =
[
    ("copy", CopyExample.RunAsync),
    ("hash", HashExample.RunAsync),
    ("replicate", ReplicationExample.RunAsync),
    ("broadcast", BroadcastExample.RunAsync),
    ("random-access", RandomAccessExample.RunAsync),
];

if (args.Length > 1 || (args.Length == 1 && !examples.Any(example => example.Name == args[0])))
{
    Console.Error.WriteLine("Choose copy, hash, replicate, broadcast, or random-access; omit the argument to run all five.");
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
