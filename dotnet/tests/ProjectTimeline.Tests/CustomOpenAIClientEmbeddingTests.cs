using System.Net;
using System.Text;
using System.Text.Json;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class CustomOpenAIClientEmbeddingTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    [Theory]
    [InlineData(1536, 0.01f, true)]
    [InlineData(1535, 0.01f, false)]
    [InlineData(1537, 0.01f, false)]
    [InlineData(1536, 0f, false)]
    public async Task Sdk_embedding_request_and_vector_validation(int dimension, float value, bool valid)
    {
        var calls = 0;
        using var transport = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            Assert.EndsWith("/embeddings", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("text-embedding-3-small", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(1536, body.RootElement.GetProperty("dimensions").GetInt32());
            Assert.Contains("A claim", body.RootElement.GetProperty("input").ToString());
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "text-embedding-3-small",
                data = new[] { new { index = 0, embedding = Enumerable.Repeat(value, dimension) } }
            }), Encoding.UTF8, "application/json") };
        }));
        var client = new CustomOpenAIClient("offline", transport);
        if (valid) Assert.Equal(1536, (await client.EmbedAsync("A claim", default)).Length);
        else Assert.Equal("InvalidResponse", (await Assert.ThrowsAsync<TimelineFactProviderException>(() => client.EmbedAsync("A claim", default))).Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Sdk_retries_are_disabled_and_errors_are_safe()
    {
        var calls = 0;
        using var transport = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("PRIVATE") });
        }));
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => new CustomOpenAIClient("offline", transport).EmbedAsync("claim", default));
        Assert.Equal("RateLimited", error.Code);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Fact]
    public async Task Cancellation_and_input_validation_do_not_send()
    {
        using var transport = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected transport call")));
        var client = new CustomOpenAIClient("offline", transport);
        await Assert.ThrowsAsync<TimelineFactProviderException>(() => client.EmbedAsync(new string('x', 4001), default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EmbedAsync("claim", new CancellationToken(true)));
    }

    [Fact]
    public async Task Inflight_cancellation_reaches_sdk_transport_without_retry()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var transport = new HttpClient(new Handler(async (_, ct) =>
        {
            calls++;
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            throw new Xunit.Sdk.XunitException("Cancellation was not forwarded");
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CustomOpenAIClient("offline", transport).EmbedAsync("claim", cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task Nonfinite_sdk_vectors_are_rejected(float value)
    {
        var values = Enumerable.Repeat(0.01f, 1536).ToArray();
        values[12] = value;
        // SDK accepts its native base64 encoding; this also exercises nonfinite values without nonstandard JSON numbers.
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        using var transport = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model = "text-embedding-3-small",
                data = new[] { new { index = 0, embedding = Convert.ToBase64String(bytes) } } }), Encoding.UTF8, "application/json")
        })));
        Assert.Equal("InvalidResponse", (await Assert.ThrowsAsync<TimelineFactProviderException>(() =>
            new CustomOpenAIClient("offline", transport).EmbedAsync("claim", default))).Code);
    }
}
