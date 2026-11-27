# Hash a forward-only Azure Blob upload in one pass

Give the Azure SDK a `HashingReadStream` over the incoming stream. A typical ASP.NET Core endpoint wraps `HttpRequest.Body`, keeps the request stream open, and passes `HttpContext.RequestAborted` to the upload.

```csharp
await using var body = new HashingReadStream(request.Body, TeeHashAlgorithm.SHA256, leaveOpen: true);
await Upload.CopyAsync(body, blob, request.HttpContext.RequestAborted);
long bytes = body.BytesRead;
string sha256 = body.Results[TeeHashAlgorithm.SHA256].Hex;
```

The Azure SDK pulls the source forward and buffers blocks for transfer. The sample limits concurrent transfers to one and writes to a new blob. It returns the count and digest after Azure confirms the upload and the reader has reached source EOF. If a source read fails or the caller stops early, the digest stays incomplete. A digest that completed before a storage error describes the source bytes. Only a successful upload confirms the storage commit.

## Upload from standard input

Set `AZURE_STORAGE_CONNECTION_STRING` for an account you can write to and supply an existing container and unused blob name.

```sh
dotnet run -c Release --project samples/TeeForge.BlobUpload -- my-container my-new-blob < input.bin
```

## Run the local integration check

Start the Azurite blob service on its default loopback port with its development account. In a second terminal run the smoke command.

```sh
npx --yes --package azurite@3.38.0 azurite-blob --blobHost 127.0.0.1 --disableTelemetry
```

```sh
dotnet run -c Release --project samples/TeeForge.BlobUpload -- --smoke
```

The check uploads and reads back a 5 MiB object, verifies its SHA-256 and byte count, and injects an I/O error before a second upload reaches EOF. It checks the partial byte count, incomplete digest, and absence of a committed blob. It removes its temporary container afterward.
