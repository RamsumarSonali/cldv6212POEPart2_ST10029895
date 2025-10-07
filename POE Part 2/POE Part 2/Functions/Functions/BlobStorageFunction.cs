// FUNCTION 2: Write to Blob Storage
using System.Net;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
namespace ABCRetailersFunctions.Functions
{
    public class BlobStorageFunction
    {
        private readonly ILogger _logger;
        private readonly BlobServiceClient _blobServiceClient;

        public BlobStorageFunction(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<BlobStorageFunction>();

            var connectionString = Environment.GetEnvironmentVariable("AzureStorageConnection")
                ?? throw new InvalidOperationException("AzureStorageConnection not configured");

            _blobServiceClient = new BlobServiceClient(connectionString);
        }

        [Function("UploadProductImage")]
        public async Task<HttpResponseData> UploadProductImage(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "images/product")] HttpRequestData req)
        {
            _logger.LogInformation("UploadProductImage function triggered");

            try
            {
                var formData = await ParseMultipartFormDataAsync(req);

                if (formData == null || !formData.Files.Any())
                {
                    var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badResponse.WriteStringAsync("No file uploaded");
                    return badResponse;
                }

                var file = formData.Files.First();
                var containerClient = _blobServiceClient.GetBlobContainerClient("product-images");
                await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);

                var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
                var blobClient = containerClient.GetBlobClient(fileName);

                using var stream = file.Content;
                await blobClient.UploadAsync(stream, overwrite: true);

                var imageUrl = blobClient.Uri.ToString();
                _logger.LogInformation($"Image uploaded successfully: {imageUrl}");

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    imageUrl = imageUrl,
                    fileName = fileName
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error uploading image: {ex.Message}");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync($"Error: {ex.Message}");
                return errorResponse;
            }
        }

        [Function("UploadPaymentProof")]
        public async Task<HttpResponseData> UploadPaymentProof(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "files/payment-proof")] HttpRequestData req)
        {
            _logger.LogInformation("UploadPaymentProof function triggered");

            try
            {
                var formData = await ParseMultipartFormDataAsync(req);

                if (formData == null || !formData.Files.Any())
                {
                    var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badResponse.WriteStringAsync("No file uploaded");
                    return badResponse;
                }

                var file = formData.Files.First();
                var containerClient = _blobServiceClient.GetBlobContainerClient("payment-proofs");
                await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

                var fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{file.FileName}";
                var blobClient = containerClient.GetBlobClient(fileName);

                using var stream = file.Content;
                await blobClient.UploadAsync(stream, overwrite: true);

                _logger.LogInformation($"Payment proof uploaded successfully: {fileName}");

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    fileName = fileName,
                    message = "Payment proof uploaded successfully"
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error uploading payment proof: {ex.Message}");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync($"Error: {ex.Message}");
                return errorResponse;
            }
        }

        // Helper classes and method for parsing multipart form data
        private class FormFile
        {
            public string FileName { get; set; }
            public Stream Content { get; set; }
        }

        private class FormData
        {
            public List<FormFile> Files { get; set; } = new List<FormFile>();
        }

        private async Task<FormData> ParseMultipartFormDataAsync(HttpRequestData req)
        {
            var contentType = req.Headers.GetValues("Content-Type").FirstOrDefault();
            if (string.IsNullOrEmpty(contentType) || !contentType.StartsWith("multipart/form-data"))
                return null;

            var boundary = GetBoundary(contentType);
            if (string.IsNullOrEmpty(boundary))
                return null;

            var formData = new FormData();
            var reader = new StreamReader(req.Body);
            var bodyBytes = await ReadToEndAsync(req.Body);

            var multipart = new MultipartReader(boundary, new MemoryStream(bodyBytes));
            MultipartSection section;
            while ((section = await multipart.ReadNextSectionAsync()) != null)
            {
                var contentDisposition = section.GetContentDispositionHeader();
                if (contentDisposition != null && contentDisposition.IsFileDisposition())
                {
                    var fileName = contentDisposition.FileName.Value ?? contentDisposition.FileNameStar.Value;
                    var ms = new MemoryStream();
                    await section.Body.CopyToAsync(ms);
                    ms.Position = 0;
                    formData.Files.Add(new FormFile
                    {
                        FileName = fileName,
                        Content = ms
                    });
                }
            }
            return formData;
        }

        private static string GetBoundary(string contentType)
        {
            var elements = contentType.Split(';');
            var boundaryElement = elements.FirstOrDefault(e => e.Trim().StartsWith("boundary=", StringComparison.OrdinalIgnoreCase));
            if (boundaryElement != null)
            {
                var boundary = boundaryElement.Substring(boundaryElement.IndexOf('=') + 1).Trim();
                if (boundary.Length >= 2 && boundary[0] == '"' && boundary[boundary.Length - 1] == '"')
                {
                    boundary = boundary.Substring(1, boundary.Length - 2);
                }
                return boundary;
            }
            return null;
        }

        private static async Task<byte[]> ReadToEndAsync(Stream stream)
        {
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms);
                return ms.ToArray();
            }
        }
    }
}