using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using ProjectTimeline.EvaluationLabeler;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class EvaluationLabelerHttpTests
{
    [Fact]
    public async Task RazorShowsExactLongQuestionAndAnswerAsEncodedTextWithoutAutomaticLabels()
    {
        var f = new LabelerFixture();
        var question = "\n<script>alert('question')</script> & \"quoted\" 日本語";
        var answer = "\n<img src=x onerror=alert(1)>\n" + new string('x', 45001) + "END-OF-FULL-ANSWER";
        f.WriteRows(LabelerFixture.Row(1, question, answer));
        await using var host = await LabelerHttpHost.Start(f);
        var response = await host.Client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(question, TextInPre(html, "question"));
        Assert.Equal(answer, TextInPre(html, "response"));
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("Recorded outcome: Returned", html);
        Assert.Contains("Research status: Partial", html);
        Assert.Contains("0 / 1 labeled", html);
        Assert.Contains("Complete response JSON", html);
        Assert.Contains("Complete raw record JSON", html);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(File.Exists(f.Labels));
    }

    [Theory]
    [InlineData("[{\"header\":\"type\",\"value\":\"happy_path\"}]", "happy_path")]
    [InlineData("[null,42,{}, {\"header\":\"other\",\"value\":\"ignored\"},{\"header\":\"TYPE\",\"value\":\"  Exact Value  \"}]", "  Exact Value  ")]
    [InlineData("[{\"header\":\"type\",\"value\":\"<img src=x onerror=alert(1)> & \\\"quoted\\\"\"}]", "<img src=x onerror=alert(1)> & \"quoted\"")]
    [InlineData(null, "Not available")]
    [InlineData("null", "Not available")]
    [InlineData("{}", "Not available")]
    [InlineData("\"invalid\"", "Not available")]
    [InlineData("[]", "Not available")]
    [InlineData("[null,42,\"type\",{}]", "Not available")]
    [InlineData("[{\"header\":null,\"value\":\"ignored\"}]", "Not available")]
    [InlineData("[{\"header\":42,\"value\":\"ignored\"}]", "Not available")]
    [InlineData("[{\"header\":\"other\",\"value\":\"ignored\"}]", "Not available")]
    [InlineData("[{\"header\":\"type\"}]", "Not available")]
    [InlineData("[{\"header\":\"type\",\"value\":null}]", "Not available")]
    [InlineData("[{\"header\":\"type\",\"value\":42}]", "Not available")]
    [InlineData("[{\"header\":\"type\",\"value\":{}}]", "Not available")]
    [InlineData("[{\"header\":\"type\",\"value\":\"\"}]", "Not available")]
    [InlineData("[{\"header\":\"type\",\"value\":\" \\t\"}]", "Not available")]
    public async Task ScenarioTypeIsVisibleEncodedAndOptionalPerRecord(string? fields, string expected)
    {
        var f = new LabelerFixture();
        var row = JsonNode.Parse(LabelerFixture.Row(1))!;
        row["scenario"]!["type"] = "not-the-field-type";
        if (fields is not null) row["scenario"]!["fields"] = JsonNode.Parse(fields);
        f.WriteRows(row.ToJsonString(), LabelerFixture.Row(2));
        await using var host = await LabelerHttpHost.Start(f);
        var html = await host.Page("/?record=0");
        const string marker = "<span id=\"scenario-type\">";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Scenario type must be visible in its metadata span.");
        var end = html.IndexOf("</span>", start, StringComparison.Ordinal);
        var encoded = html[(start + marker.Length)..end];
        Assert.Equal(expected, WebUtility.HtmlDecode(encoded));
        Assert.DoesNotContain("<", encoded);
        Assert.Contains("Scenario type:", html[..start]);
        Assert.True(end < html.IndexOf("<h2 id=\"question-title\">", StringComparison.Ordinal));
        Assert.Contains(marker + "Not available</span>", await host.Page("/?record=1"));
        Assert.Contains(marker + encoded + "</span>", await host.Page("/?record=0"));
        Assert.Contains("0 / 2 labeled", html);
        Assert.False(File.Exists(f.Labels));
    }

    [Fact]
    public async Task LegacyNullResponseShowsNoInventedAnswerAndKeepsSavedFailureAndMessages()
    {
        var f = new LabelerFixture();
        f.WriteRows(LabelerFixture.Row(1, content: null));
        await using var host = await LabelerHttpHost.Start(f);
        var html = await host.Page();
        Assert.Contains("No final answer was saved", html);
        Assert.Contains("LegacyFailure", html);
        Assert.Contains("Saved trace", html);
        Assert.DoesNotContain("id=\"response\"", html);
        Assert.Contains("0 / 1 labeled", html);
    }

    [Fact]
    public async Task OneClickBadThenCategoryThenGoodPersistImmediatelyAndResumeWithoutAdvance()
    {
        var f = new LabelerFixture();
        await using (var host = await LabelerHttpHost.Start(f))
        {
            await host.Page();
            var bad = await host.Post("Bad", "uncategorized", 0);
            Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
            using var result = JsonDocument.Parse(await bad.Content.ReadAsStringAsync());
            Assert.Equal(1, result.RootElement.GetProperty("pending").GetInt32());
            Assert.Equal(0, result.RootElement.GetProperty("record").GetInt32());
            Assert.Contains("uncategorized", File.ReadAllText(f.Labels));
            Assert.Contains("data-record=\"1\"", await host.Page()); // first unlabeled on a fresh load
            var same = await host.Page("/?record=0");
            Assert.Contains("Bad — uncategorized", WebUtility.HtmlDecode(same));
            Assert.Equal(HttpStatusCode.OK, (await host.Post("Bad", "hallucination", 1)).StatusCode);
            Assert.Contains("hallucination", File.ReadAllText(f.Labels));
            Assert.Equal(HttpStatusCode.OK, (await host.Post("Good", "", 2)).StatusCode);
            Assert.DoesNotContain("hallucination", File.ReadAllText(f.Labels));
            Assert.Equal(HttpStatusCode.Conflict, (await host.Post("Bad", "other", 2)).StatusCode);
            Assert.Contains("Good", File.ReadAllText(f.Labels));
        }
        await using var restart = await LabelerHttpHost.Start(f);
        Assert.Contains("1 / 2 labeled", await restart.Page());
        Assert.Contains("data-record=\"1\"", await restart.Page());
        Assert.Contains("Loaded saved label", await restart.Page("/?record=0"));
    }

    [Fact]
    public async Task AllLabeledResumesAtPendingCategory()
    {
        var f = new LabelerFixture();
        using (var store = new LabelStore(f.Load(), f.Labels))
        {
            store.Save(0, "Good", "", 0);
            store.Save(1, "Bad", "uncategorized", 0);
        }
        await using var host = await LabelerHttpHost.Start(f);
        var html = await host.Page();
        Assert.Contains("data-record=\"1\"", html);
        Assert.Contains("2 / 2 labeled", html);
        Assert.Contains("1 Bad needs category", html);
    }

    [Theory]
    [InlineData("missing-token", 400)]
    [InlineData("wrong-token", 400)]
    [InlineData("missing-cookie", 400)]
    [InlineData("foreign-origin", 403)]
    [InlineData("missing-origin", 403)]
    [InlineData("foreign-host", 400)]
    [InlineData("wrong-port", 400)]
    [InlineData("extra-path", 400)]
    [InlineData("extra-question", 400)]
    [InlineData("duplicate-record", 400)]
    [InlineData("unknown-label", 400)]
    [InlineData("invalid-category", 400)]
    [InlineData("good-with-category", 400)]
    [InlineData("outside-selection", 400)]
    public async Task WriteAttacksCannotAnnotateOrChoosePaths(string attack, int expected)
    {
        var f = new LabelerFixture();
        var input = File.ReadAllBytes(f.Input);
        await using var host = await LabelerHttpHost.Start(f);
        await host.Page();
        var fields = host.Fields("Bad", "uncategorized", 0);
        switch (attack)
        {
            case "missing-token": fields.RemoveAll(x => x.Key == "__RequestVerificationToken"); break;
            case "wrong-token": fields.RemoveAll(x => x.Key == "__RequestVerificationToken"); fields.Add(new("__RequestVerificationToken", "wrong")); break;
            case "extra-path": fields.Add(new("path", "../../human.csv")); break;
            case "extra-question": fields.Add(new("question", "Injected")); break;
            case "duplicate-record": fields.Add(new("record", "1")); break;
            case "unknown-label": fields.RemoveAll(x => x.Key == "label"); fields.Add(new("label", "Returned")); break;
            case "invalid-category": fields.RemoveAll(x => x.Key == "category"); fields.Add(new("category", "other,hallucination")); break;
            case "good-with-category": fields.RemoveAll(x => x.Key == "label"); fields.Add(new("label", "Good")); break;
            case "outside-selection": fields.RemoveAll(x => x.Key == "record"); fields.Add(new("record", "-1")); break;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/?handler=Label") { Content = new FormUrlEncodedContent(fields) };
        if (attack != "missing-cookie") request.Headers.Add("Cookie", host.Cookie);
        if (attack != "missing-origin") request.Headers.Add("Origin", attack == "foreign-origin" ? "http://evil.test" : LabelerHttpHost.Origin);
        if (attack == "foreign-host") request.Headers.Host = "evil.test:5198";
        if (attack == "wrong-port") request.Headers.Host = "localhost:1234";
        var response = await host.Client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.False(File.Exists(f.Labels));
        Assert.Equal(input, File.ReadAllBytes(f.Input));
    }

    [Theory]
    [InlineData("/?path=../../secret")]
    [InlineData("/?record=1000")]
    [InlineData("/?record=0&record=1")]
    [InlineData("/?handler=Download&path=anything")]
    public async Task GetCannotSelectArbitraryFiles(string path)
    {
        await using var host = await LabelerHttpHost.Start(new LabelerFixture());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("/labeler.js", "text/javascript", "__RequestVerificationToken")]
    [InlineData("/labeler.css", "text/css", "pre-wrap")]
    public async Task EmbeddedSelfAssetsWorkOutsideProjectWorkingDirectory(string path, string media, string marker)
    {
        await using var host = await LabelerHttpHost.Start(new LabelerFixture());
        var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(media, response.Content.Headers.ContentType!.MediaType);
        Assert.Contains(marker, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BuiltDllServesRazorAndEmbeddedAssetsFromRepositoryRoot()
    {
        var f = new LabelerFixture();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "dotnet", "ProjectTimeline.sln"))) root = root.Parent;
        Assert.NotNull(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root.FullName, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Use the executable assembly, not WebApi startup or dotnet run/project content discovery.
        foreach (var arg in new[] { typeof(LabelerApp).Assembly.Location, "--input", f.Input, "--labels", f.Labels, "--port", port.ToString() })
            start.ArgumentList.Add(arg);
        start.Environment["ASPNETCORE_URLS"] = "http://192.0.2.1:1"; // Must be ignored, not bound.
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
                { BaseAddress = new Uri($"http://localhost:{port}"), Timeout = TimeSpan.FromSeconds(2) };
            HttpResponseMessage? page = null;
            for (var attempt = 0; attempt < 200 && page is null; attempt++)
            {
                if (process.HasExited) throw new InvalidOperationException("Labeler exited: " + await stderr);
                try { page = await client.GetAsync("/"); }
                catch (HttpRequestException) { await Task.Delay(100); }
            }
            Assert.NotNull(page);
            using (page)
            {
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                var html = await page.Content.ReadAsStringAsync();
                Assert.Equal("Same question", TextInPre(html, "question"));
                Assert.Equal("Full answer", TextInPre(html, "response"));
                Assert.Contains("__RequestVerificationToken", html);
            }
            Assert.Contains("__RequestVerificationToken", await client.GetStringAsync("/labeler.js"));
            Assert.Contains("pre-wrap", await client.GetStringAsync("/labeler.css"));
            Assert.False(File.Exists(f.Labels));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true); // Only the process this test owns.
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Fact]
    public async Task DownloadReturnsOnlyConfirmedCsvAndRejectsExternalChanges()
    {
        var f = new LabelerFixture();
        await using var host = await LabelerHttpHost.Start(f);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/?handler=Download")).StatusCode);
        Assert.False(File.Exists(f.Labels));
        Assert.Contains("id=\"download\"", await host.Page());
        Assert.Equal(HttpStatusCode.OK, (await host.Post("Bad", "uncategorized", 0)).StatusCode);
        var download = await host.Client.GetAsync("/?handler=Download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("text/csv", download.Content.Headers.ContentType!.MediaType);
        Assert.Contains("evaluation-labels.csv", download.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal(File.ReadAllBytes(f.Labels), await download.Content.ReadAsByteArrayAsync());
        File.AppendAllText(f.Labels, "External edit");
        var bytes = File.ReadAllBytes(f.Labels);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.GetAsync("/?handler=Download")).StatusCode);
        Assert.Equal(bytes, File.ReadAllBytes(f.Labels));
    }

    [Fact]
    public async Task ExternalCsvChangeReturnsFriendlyConflictWithoutClobber()
    {
        var f = new LabelerFixture();
        await using var host = await LabelerHttpHost.Start(f);
        await host.Page();
        Assert.Equal(HttpStatusCode.OK, (await host.Post("Good", "", 0)).StatusCode);
        File.AppendAllText(f.Labels, "External edit");
        var bytes = File.ReadAllBytes(f.Labels);
        var response = await host.Post("Bad", "other", 1);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Reload before editing", await response.Content.ReadAsStringAsync());
        Assert.Equal(bytes, File.ReadAllBytes(f.Labels));
    }

    private static string TextInPre(string html, string id)
    {
        var start = html.IndexOf("<pre id=\"" + id + "\"", StringComparison.Ordinal);
        var span = html.IndexOf("<span>", start, StringComparison.Ordinal) + "<span>".Length;
        return WebUtility.HtmlDecode(html[span..html.IndexOf("</span>", span, StringComparison.Ordinal)]);
    }
}

internal sealed class LabelerHttpHost(WebApplication app, HttpClient client) : IAsyncDisposable
{
    public const string Origin = "http://localhost:5198";
    public HttpClient Client { get; } = client;
    public string Token { get; private set; } = "";
    public string Cookie { get; private set; } = "";
    public static async Task<LabelerHttpHost> Start(LabelerFixture fixture)
    {
        var app = LabelerApp.Create(new(fixture.Input, fixture.Labels), b => b.WebHost.UseTestServer());
        await app.StartAsync();
        var client = app.GetTestClient();
        client.BaseAddress = new Uri(Origin);
        return new(app, client);
    }
    public async Task<string> Page(string path = "/")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (Cookie.Length > 0) request.Headers.Add("Cookie", Cookie);
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        if (response.Headers.TryGetValues("Set-Cookie", out var values)) Cookie = values.First().Split(';')[0];
        var html = await response.Content.ReadAsStringAsync();
        var name = html.IndexOf("name=\"__RequestVerificationToken\"", StringComparison.Ordinal);
        var start = html.IndexOf("value=\"", name, StringComparison.Ordinal) + "value=\"".Length;
        Token = WebUtility.HtmlDecode(html[start..html.IndexOf('"', start)]);
        return html;
    }
    public List<KeyValuePair<string, string>> Fields(string label, string category, long revision)
        => [new("record", "0"), new("label", label), new("category", category),
            new("expectedRevision", revision.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("__RequestVerificationToken", Token)];
    public async Task<HttpResponseMessage> Post(string label, string category, long revision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/?handler=Label") { Content = new FormUrlEncodedContent(Fields(label, category, revision)) };
        request.Headers.Add("Cookie", Cookie);
        request.Headers.Add("Origin", Origin);
        return await Client.SendAsync(request);
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
    }
}
