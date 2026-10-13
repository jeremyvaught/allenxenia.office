using System.Net;
using System.Text.Json;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Infrastructure.Assistant;

namespace AllenBuilding.IntegrationTests;

public sealed class OpenAiAssistantModelTests
{
    [Fact]
    public async Task Bounded_stateless_request_pins_model_schema_and_storage_without_executable_tools()
    {
        var id = Guid.NewGuid(); var sent = "";
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            sent = await request.Content!.ReadAsStringAsync();
            return Response(new { answer = "Published guidance", sources = new[] { id.ToString() }, route = "help" });
        }));
        var result = await new OpenAiAssistantModel(client, "synthetic-key").AnswerAsync(new("Where is entry?", [new(id, Guid.NewGuid(), "Entry", "Published directions")], false), default);
        Assert.Equal(AssistantModelCode.Success, result.Code); Assert.Equal(id, Assert.Single(result.Sources!));
        using var body = JsonDocument.Parse(sent); var root = body.RootElement;
        Assert.Equal(OpenAiAssistantModel.Model, root.GetProperty("model").GetString()); Assert.False(root.GetProperty("store").GetBoolean()); Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.TryGetProperty("tools", out _)); Assert.False(root.TryGetProperty("previous_response_id", out _));
        Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        Assert.False(root.GetProperty("text").GetProperty("format").GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        Assert.DoesNotContain("synthetic-key", sent);
    }
    [Theory]
    [InlineData("foreign-source")]
    [InlineData("destination")]
    [InlineData("extra-action")]
    [InlineData("incomplete")]
    [InlineData("refusal")]
    [InlineData("oversize")]
    [InlineData("provider-failure")]
    public async Task Untrusted_or_failed_provider_output_does_not_become_an_answer(string failure)
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(failure switch
        {
            "foreign-source" => Response(new { answer = "private-canary", sources = new[] { Guid.NewGuid().ToString() }, route = "help" }),
            "destination" => Response(new { answer = "private-canary", sources = Array.Empty<string>(), route = "https://untrusted.test" }),
            "extra-action" => Response(new { answer = "private-canary", sources = Array.Empty<string>(), route = "help", execute = true }),
            "incomplete" => Response(new { answer = "private-canary", sources = Array.Empty<string>(), route = "help" }, "incomplete"),
            "refusal" => new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"private-canary\"}]}]}") },
            "oversize" => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 131073)) },
            _ => new(HttpStatusCode.TooManyRequests) { Content = new StringContent("private-canary") }
        })));
        var result = await new OpenAiAssistantModel(client, "synthetic-key").AnswerAsync(new("Question", [], false), default);
        Assert.NotEqual(AssistantModelCode.Success, result.Code); Assert.Empty(result.Answer);
    }
    [Fact]
    public async Task Input_budget_prevents_network_use_and_cancellation_propagates()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ => { calls++; throw new OperationCanceledException(); }));
        var model = new OpenAiAssistantModel(client, "synthetic-key");
        Assert.Equal(AssistantModelCode.Invalid, (await model.AnswerAsync(new(new string('x', 4001), [], false), default)).Code);
        Assert.Equal(AssistantModelCode.Invalid, (await model.AnswerAsync(new(new string('漢', 4000), [], false), default)).Code);
        Assert.Equal(0, calls);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.AnswerAsync(new("Question", [], false), cancelled.Token));
    }
    private static HttpResponseMessage Response(object answer, string status = "completed") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            status,
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(answer) } } } },
            usage = new { input_tokens = 100, output_tokens = 30 }
        }))
    };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return respond(request); }
    }
}
