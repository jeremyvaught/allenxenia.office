using System.Net;
using System.Text.Json;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Infrastructure.Assistant;

namespace AllenBuilding.IntegrationTests;

public sealed class OpenAiAssistantModelTests
{
    [Fact]
    public async Task Followup_payload_has_bounded_untrusted_exchanges_without_provider_stored_conversations_or_tools()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(async request =>
        {
            calls++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var root = body.RootElement;
            Assert.Contains("prior exchanges", root.GetProperty("instructions").GetString());
            using var input = JsonDocument.Parse(root.GetProperty("input").GetString()!);
            Assert.Equal("The sink leaks", input.RootElement.GetProperty("History")[0].GetProperty("Question").GetString());
            Assert.Equal("Question", input.RootElement.EnumerateObject().Last().Name);
            Assert.Equal("Kitchen", input.RootElement.GetProperty("Question").GetString());
            Assert.Contains("newest user message", root.GetProperty("instructions").GetString());
            Assert.False(root.GetProperty("store").GetBoolean()); Assert.False(root.TryGetProperty("previous_response_id", out _)); Assert.False(root.TryGetProperty("tools", out _));
            return Response(new { answer = "Open building help", sources = Array.Empty<string>(), route = "help" });
        }));
        var model = new OpenAiAssistantModel(client, "synthetic-key");
        Assert.Equal(AssistantModelCode.Success, (await model.AnswerAsync(new("Kitchen", [], false, History: [new("The sink leaks", "Where?")]), default)).Code);
        Assert.Equal(AssistantModelCode.Invalid, (await model.AnswerAsync(new("Kitchen", [], false, History: Enumerable.Repeat(new AssistantExchange("Question", "Answer"), 6).ToArray()), default)).Code);
        Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Private_preparation_schema_allows_only_the_selected_draft_kind_and_no_execution(bool comment)
    {
        using var client = new HttpClient(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var root = body.RootElement;
            Assert.False(root.TryGetProperty("tools", out _));
            var types = root.GetProperty("text").GetProperty("format").GetProperty("schema").GetProperty("properties").GetProperty("draft").GetProperty("anyOf")[1]
                .GetProperty("properties").GetProperty("kind").GetProperty("enum");
            Assert.Equal(comment ? "comment" : "request", Assert.Single(types.EnumerateArray()).GetString());
            return Response(new
            {
                answer = "Open the review to confirm",
                sources = Array.Empty<string>(),
                route = comment ? "requests" : "new_request",
                draft = new { kind = comment ? "comment" : "request", title = comment ? "" : "Sink leak", description = comment ? "" : "Water under the basin", location = comment ? "" : "Kitchen", comment = comment ? "Call first" : "" }
            });
        }));
        var result = await new OpenAiAssistantModel(client, "synthetic-key").AnswerAsync(new("Prepare my maintenance action", [], true, true, comment), default);
        Assert.Equal(AssistantModelCode.Success, result.Code); Assert.NotNull(result.Draft); Assert.Equal(comment ? "Call first" : "", result.Draft.Comment);
    }
    [Theory]
    [InlineData("guest")]
    [InlineData("wrong-kind")]
    [InlineData("execute")]
    public async Task Unregistered_or_executable_drafts_are_rejected(string failure)
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(Response(new
        {
            answer = "untrusted",
            sources = Array.Empty<string>(),
            route = "new_request",
            draft = new { kind = failure == "execute" ? "execute" : "comment", title = "", description = "", location = "", comment = "Call first" }
        }))));
        var result = await new OpenAiAssistantModel(client, "synthetic-key").AnswerAsync(new("Question", [], failure != "guest", failure != "guest"), default);
        Assert.NotEqual(AssistantModelCode.Success, result.Code); Assert.Null(result.Draft);
    }
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
        Assert.False(root.GetProperty("text").GetProperty("format").GetProperty("schema").GetProperty("properties").TryGetProperty("draft", out _));
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
