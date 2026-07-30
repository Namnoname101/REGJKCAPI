using System.Net;
using System.Text;
using System.Text.Json;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var successHandler = new QueueHttpMessageHandler(
[
    JsonResponse("""{"errorId":0,"taskId":"task-123"}"""),
    JsonResponse("""{"errorId":0,"status":"processing"}"""),
    JsonResponse("""{"errorId":0,"status":"ready","solution":{"text":"4911"}}""")
]);
var successSolver = CreateSolver(successHandler);
var success = await successSolver.SolveAsync(
    "AQIDBA==",
    CancellationToken.None);

if (!success.IsSuccess || success.Text != "4911" || success.TaskId != "task-123")
{
    throw new InvalidOperationException(
        $"Expected a successful 4911 solution but got: {success}");
}

if (successHandler.Requests.Count != 3)
{
    throw new InvalidOperationException(
        $"Expected createTask plus two polls, got {successHandler.Requests.Count} requests.");
}

using (var createDocument = JsonDocument.Parse(successHandler.Requests[0].Body))
{
    var task = createDocument.RootElement.GetProperty("task");
    if (task.GetProperty("type").GetString() != "ImageToTextTask"
        || task.GetProperty("imageBase64").GetString() != "AQIDBA=="
        || task.GetProperty("module").GetString() != "module_1")
    {
        throw new InvalidOperationException("createTask payload did not match the OMO ImageToText contract.");
    }
}

var errorHandler = new QueueHttpMessageHandler(
[
    JsonResponse(
        """{"errorId":1,"errorCode":"ERROR_ZERO_BALANCE","errorDescription":"Balance is empty"}""")
]);
var failed = await CreateSolver(errorHandler).SolveAsync(
    "CQgH",
    CancellationToken.None);
if (failed.IsSuccess
    || !failed.Message.Contains("ERROR_ZERO_BALANCE", StringComparison.Ordinal))
{
    throw new InvalidOperationException("OMO error envelopes were not handled correctly.");
}

Console.WriteLine(
    "PASS OMOCaptcha ImageToTextTask protocol: payload, processing polling, ready solution, and error envelope.");
return 0;

static OmoImageCaptchaSolver CreateSolver(QueueHttpMessageHandler handler) =>
    new(
        new SingleHttpClientFactory(new HttpClient(handler)),
        Options.Create(new OmoCaptchaOptions
        {
            Enabled = true,
            ApiKey = "OMO_DIAGNOSTIC_KEY",
            Module = "module_1",
            TimeoutSeconds = 15,
            InitialPollingDelayMilliseconds = 1,
            MaxPollingDelayMilliseconds = 1
        }),
        NullLogger<OmoImageCaptchaSolver>.Instance);

static HttpResponseMessage JsonResponse(string json) =>
    new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

file sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

file sealed class QueueHttpMessageHandler(
    IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);

    public List<CapturedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new CapturedRequest(
            request.RequestUri?.ToString() ?? string.Empty,
            body));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No fake OMOCaptcha response remains.");
        }

        return _responses.Dequeue();
    }
}

file sealed record CapturedRequest(string Url, string Body);
