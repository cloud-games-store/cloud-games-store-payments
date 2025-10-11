using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace Payments;

public enum PaymentStatus
{
    PaymentApproved = 1,
    PaymentRejected = 2
}

#region Models
public class PaymentEvent : ITableEntity
{
    public string PartitionKey { get; set; } = default!;
    public string RowKey { get; set; } = default!;
    public PaymentStatus EventType { get; set; } = default!;
    public bool PaymentApproved { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = default!;
    public string Message { get; set; } = string.Empty;
    public DateTime EventTime { get; set; } = DateTime.UtcNow;

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
}

public class PaymentDetails 
{ 
    public string Method { get; set; } = string.Empty; 
    public string Token { get; set; } = string.Empty; 
}

public class RequestModel 
{ 
    public PaymentDetails Payment { get; set; } 
    public decimal? TotalAmount { get; set; } 
    public string TransactionId { get; set; } 
}

public class ResponseModel 
{
    public bool Success { get; set; } 
    public string TransactionId { get; set; } = string.Empty; 
    public string Message { get; set; } = string.Empty; 
}
#endregion

public class PaymentFunction
{
    private readonly IConfiguration _configuration;
    private static readonly Random random = new Random();

    public PaymentFunction(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [Function("payment-process")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
    {
        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        RequestModel? request = null;

        try
        {
            request = JsonConvert.DeserializeObject<RequestModel>(requestBody) ?? throw new Exception("Payload inválido.");

            var paymentTokenValid = !string.IsNullOrWhiteSpace(request.Payment.Token);
            var paymentMethodValid = !string.IsNullOrWhiteSpace(request.Payment.Method);
            var paymentAmountValid = request.TotalAmount.HasValue || request.TotalAmount.Value > 0;

            if (!paymentTokenValid || !paymentMethodValid || !paymentAmountValid)
                return new BadRequestObjectResult(new ResponseModel 
                { 
                    Success = false,
                    TransactionId = request.TransactionId, 
                    Message = "Invalid payment body: Token, Method or Amount." 
                });

            var pagamentoValido = random.Next(0, 2) == 1;

            ResponseModel response = new ResponseModel 
            {
                Success = pagamentoValido, 
                TransactionId = request.TransactionId,
                Message = "Processing completed successfully."
            };

            await AddEventSourcing(
                request,
                response,
                pagamentoValido,
                request.TransactionId
            );

            return new OkObjectResult(response);

        } catch (Exception ex)
        {
            var response = new ResponseModel
            {
                Success = false,
                TransactionId = request.TransactionId,
                Message = $"Payment processing failed unexpectedly, {ex.Message}"
            };
            await AddEventSourcing(request, response, false, request.TransactionId);

            return new BadRequestObjectResult(response);
        }
    }

    private async Task<IActionResult> AddEventSourcing(RequestModel? request, ResponseModel response, bool pagamentoValido, string transactionId)
    {
        string connectionString = _configuration.GetValue<string>("EventStoreConnection");
        var tableClient = new TableClient(connectionString, "PaymentEvents"); 
        tableClient.CreateIfNotExists();

        var paymentEvent = new PaymentEvent 
        { 
            Method = request?.Payment.Method ?? "N/A", 
            Amount = request.TotalAmount ?? 0.0m, 
            PaymentApproved = pagamentoValido, 
            PartitionKey = transactionId,
            RowKey = Guid.NewGuid().ToString(),
            Message = response.Message,
            EventType = pagamentoValido ? PaymentStatus.PaymentApproved : PaymentStatus.PaymentRejected
        };

        await tableClient.AddEntityAsync(paymentEvent); 
        return new OkObjectResult(response);
    }
}