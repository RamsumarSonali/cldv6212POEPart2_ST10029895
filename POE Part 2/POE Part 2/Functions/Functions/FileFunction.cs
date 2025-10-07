// FUNCTION 4: Write to Azure File Shares
using ABCRetailers.Functions.Helpers;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Drawing;
using System.Net;
using static System.Web.Razor.Parser.SyntaxConstants;

namespace ABCRetailersFunctions.Functions
{
    public class FileShareFunction
    {
        private readonly ILogger _logger;
        private readonly ShareServiceClient _shareServiceClient;
        private readonly string _conn;
        private readonly string _proofs;
        private readonly string _share;
        private readonly string _shareDir;



        public FileShareFunction(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<FileShareFunction>();

            var connectionString = Environment.GetEnvironmentVariable("AzureStorageConnection")
                ?? throw new InvalidOperationException("AzureStorageConnection not configured");

            _shareServiceClient = new ShareServiceClient(connectionString);
        }

        public FileShareFunction(IConfiguration cfg)
        {
            _conn = cfg["STORAGE_CONNECTION"] ?? throw new InvalidOperationException("STORAGE_CONNECTION missing");
            _proofs = cfg["BLOB_PAYMENT_PROOFS"] ?? "payment-proofs";
            _share = cfg["FILESHARE_CONTRACTS"] ?? "contracts";
            _shareDir = cfg["FILESHARE_DIR_PAYMENTS"] ?? "payments";
        }


        [Function("UploadContractFile")]
        public async Task<HttpResponseData> Proof(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "uploads/proof-of-payment")] HttpRequestData req)
        {
            var contentType = req.Headers.TryGetValues("Content-Type", out var ct) ? ct.First() : "";
            if (!contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                return HttpJson.Bad(req, "Expected multipart/form-data");

            var form = await MultipartHelper.ParseAsync(req.Body, contentType);
            var file = form.Files.FirstOrDefault(f => f.FieldName == "ProofOfPayment");
            if (file is null || file.Data.Length == 0) return HttpJson.Bad(req, "ProofOfPayment file is required");

            var orderId = form.Text.GetValueOrDefault("OrderId");
            var customerName = form.Text.GetValueOrDefault("CustomerName");

            // Blob
            var container = new BlobContainerClient(_conn, _proofs);
            await container.CreateIfNotExistsAsync();
            var blobName = $"{Guid.NewGuid():N}-{file.FileName}";
            var blob = container.GetBlobClient(blobName);
            await using (var s = file.Data) await blob.UploadAsync(s);

            // Azure Files
            var share = new ShareClient(_conn, _share);
            await share.CreateIfNotExistsAsync();
            var root = share.GetRootDirectoryClient();
            var dir = root.GetSubdirectoryClient(_shareDir);
            await dir.CreateIfNotExistsAsync();

            var fileClient = dir.GetFileClient(blobName + ".txt");
            var meta = $"UploadedAtUtc: {DateTimeOffset.UtcNow:O}\nOrderId: {orderId}\nCustomerName: {customerName}\nBlobUrl: {blob.Uri}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(meta);
            using var ms = new MemoryStream(bytes);
            await fileClient.CreateAsync(ms.Length);
            await fileClient.UploadAsync(ms);

            return HttpJson.Ok(req, new { fileName = blobName, blobUrl = blob.Uri.ToString() });
        }

        [Function("DownloadContractFile")]
        public async Task<HttpResponseData> DownloadContractFile(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "files/contracts/{fileName}")] HttpRequestData req,
            string fileName)
        {
            _logger.LogInformation($"DownloadContractFile triggered for: {fileName}");

            try
            {
                var shareName = "contracts";
                var directoryName = "payments";

                var shareClient = _shareServiceClient.GetShareClient(shareName);
                var directoryClient = shareClient.GetDirectoryClient(directoryName);
                var fileClient = directoryClient.GetFileClient(fileName);

                var downloadResponse = await fileClient.DownloadAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", "application/octet-stream");
                response.Headers.Add("Content-Disposition", $"attachment; filename={fileName}");

                await downloadResponse.Value.Content.CopyToAsync(response.Body);

                _logger.LogInformation($"Contract file downloaded successfully: {fileName}");

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error downloading contract file: {ex.Message}");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync($"Error: {ex.Message}");
                return errorResponse;
            }
        }

        [Function("ListContractFiles")]
        public async Task<HttpResponseData> ListContractFiles(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "files/contracts")] HttpRequestData req)
        {
            _logger.LogInformation("ListContractFiles triggered");

            try
            {
                var shareName = "contracts";
                var directoryName = "payments";

                var shareClient = _shareServiceClient.GetShareClient(shareName);
                var directoryClient = shareClient.GetDirectoryClient(directoryName);

                var files = new List<string>();

                await foreach (var item in directoryClient.GetFilesAndDirectoriesAsync())
                {
                    if (!item.IsDirectory)
                    {
                        files.Add(item.Name);
                    }
                }

                _logger.LogInformation($"Found {files.Count} contract files");

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    files = files,
                    count = files.Count
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error listing contract files: {ex.Message}");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync($"Error: {ex.Message}");
                return errorResponse;
            }
        }
    }
}