using System;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Functions
{
    public class Function3
    {
        private readonly ILogger<Function3> _logger;

        public Function3(ILogger<Function3> logger)
        {
            _logger = logger;
        }

        [Function(nameof(Function3))]
        public void Run([QueueTrigger("myqueue-items", Connection = "AzureWebJobsStorage")] QueueMessage message)
        {
            _logger.LogInformation($"C# Queue trigger function processed: {message.MessageText}");
        }
    }
}
