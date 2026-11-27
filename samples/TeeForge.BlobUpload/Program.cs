using Azure.Storage.Blobs;
using TeeForge.BlobUpload;
using TeeForge.Hashing;

if (args is ["--smoke"])
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await AzuriteSmoke.RunAsync(timeout.Token);
    return 0;
}

if (args is not [var containerName, var blobName])
{
    Console.Error.WriteLine("Supply an existing container name and a new blob name, or --smoke for the local Azurite check.");
    return 1;
}

string connection = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set AZURE_STORAGE_CONNECTION_STRING for the target storage account.");
var blob = new BlobContainerClient(connection, containerName).GetBlobClient(blobName);
await using Stream input = Console.OpenStandardInput();
await using var body = new HashingReadStream(input, TeeHashAlgorithm.SHA256, leaveOpen: true);
await Upload.CopyAsync(body, blob, CancellationToken.None);
Console.WriteLine($"{body.BytesRead} bytes, SHA-256 {body.Results[TeeHashAlgorithm.SHA256].Hex}");
return 0;
