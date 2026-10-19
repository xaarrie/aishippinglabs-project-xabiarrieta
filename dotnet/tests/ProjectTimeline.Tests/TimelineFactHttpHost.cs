using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.UI.WebApi;

namespace ProjectTimeline.Tests;

internal sealed class TimelineFactHttpHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly DbContextOptions<ProjectTimelineDbContext> options = new DbContextOptionsBuilder<ProjectTimelineDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    public ProjectTimelineDbContext Db { get; }
    public HttpClient Client { get; private set; } = null!;
    public User Actor { get; } = new() { Email = "http@example.test", DisplayName = "Author", PasswordHash = "HTTP_SECRET_SENTINEL" };
    public Project Project { get; } = new() { Code = "H", Name = "HTTP" };
    public Timeline Timeline { get; } = new() { Code = "H", Name = "HTTP" };
    public TimelineEntry Entry { get; } = new() { EntryNumber = 2, Title = "Original", Content = "Alice owns delivery." };
    public FactReviewFakeAgent Agent { get; } = new();
    public bool FailFactory { get; set; }
    public int AgentConstructions { get; private set; }
    public List<string> Logs { get; } = [];
    public string Facts => $"/api/entries/{Entry.Id}/facts";
    public string Plan => $"/api/entries/{Entry.Id}/fact-proposals";
    public string Backfill => $"/api/timelines/{Timeline.Id}/fact-backfill";
    public string Accept(Guid id) => $"{Plan}/{id}/accept";

    private TimelineFactHttpHost()
    {
        Db = new(options);
        // Empty configuration sources: no application settings, user secrets, JWT keys or Program.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(new CaptureLogs(Logs));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRouting();
        builder.Services.AddScoped(_ => new ProjectTimelineDbContext(options));
        builder.Services.AddScoped(sp => new TimelineFactApplication(sp.GetRequiredService<ProjectTimelineDbContext>(), () =>
        {
            AgentConstructions++;
            if (FailFactory) throw new InvalidOperationException("HTTP_SECRET_SENTINEL provider credential");
            return Agent;
        }));
        builder.Services.AddSingleton(new TimelineFactProcessing(() => new(options), new NeverProvider()));
        app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseTimelineFactBodyBounds();
        app.MapTimelineFacts();
    }

    public static async Task<TimelineFactHttpHost> CreateAsync()
    {
        var h = new TimelineFactHttpHost();
        h.Project.OwnerId = h.Actor.Id; h.Timeline.ProjectId = h.Project.Id;
        h.Entry.TimelineId = h.Timeline.Id; h.Entry.AuthorId = h.Actor.Id;
        h.Db.AddRange(h.Actor, h.Project, h.Timeline, h.Entry);
        await h.Db.SaveChangesAsync();
        await h.app.StartAsync();
        h.Client = h.app.GetTestClient();
        h.Client.DefaultRequestHeaders.Add("X-Test-Actor", h.Actor.Id.ToString());
        return h;
    }

    public async Task<HttpResponseMessage> PostAsync(string url, string? json = null, bool unknownLength = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (json is not null) request.Content = unknownLength ? new UnknownLengthJson(json) : new StringContent(json, Encoding.UTF8, "application/json");
        if (request.Content is not null) request.Content.Headers.ContentType = new("application/json");
        if (unknownLength) request.Headers.TransferEncodingChunked = true;
        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
        await Db.Database.EnsureDeletedAsync(); // Only this fixture's unique InMemory store.
        await Db.DisposeAsync();
    }

    private sealed class UnknownLengthJson(string json) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(json)).AsTask();
    }

    private sealed class NeverProvider : ITimelineFactProvider
    {
        public string ExtractionModel => throw new InvalidOperationException("Enqueue must not resolve provider");
        public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct) => throw new InvalidOperationException("No provider calls");
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new InvalidOperationException("No provider calls");
    }

    private sealed class CaptureLogs(List<string> logs) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (logs) logs.Add(formatter(state, exception) + exception?.ToString()); }
        public void Dispose() { }
    }
}

internal sealed class HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Actor", out var actor)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actor.ToString())], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
