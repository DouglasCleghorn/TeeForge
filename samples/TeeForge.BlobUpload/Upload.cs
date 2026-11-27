using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using TeeForge.Hashing;

namespace TeeForge.BlobUpload;

internal static class Upload
{
    internal static async Task CopyAsync(HashingReadStream body, BlobClient blob, CancellationToken cancellationToken)
    {
        const int blockSize = 4 * 1024 * 1024;
        var options = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            TransferOptions = new StorageTransferOptions
            {
                InitialTransferSize = blockSize,
                MaximumTransferSize = blockSize,
                MaximumConcurrency = 1,
            },
        };

        await blob.UploadAsync(body, options, cancellationToken);
        if (!body.Results.IsComplete)
        {
            throw new EndOfStreamException("The uploader returned before observing source EOF.");
        }
    }
}
