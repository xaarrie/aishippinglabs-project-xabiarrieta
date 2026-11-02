using System.Net;
using Microsoft.AspNetCore.DataProtection;

namespace ProjectTimeline.EvaluationLabeler;

public sealed record LabelerOptions(string Input, string Labels, int Port = 5198)
{
    public static LabelerOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--input" or "--labels" or "--port")
                || !values.TryAdd(args[i], args[i + 1]) || string.IsNullOrWhiteSpace(args[i + 1]))
                throw new LabelerException("cli-options");
        }
        if (!values.TryGetValue("--input", out var input)) throw new LabelerException("cli-input-required");
        var port = 5198;
        if (values.TryGetValue("--port", out var value)
            && (!int.TryParse(value, out port) || port is < 1024 or > 65535)) throw new LabelerException("cli-port");
        return new(System.IO.Path.GetFullPath(input),
            System.IO.Path.GetFullPath(values.GetValueOrDefault("--labels") ?? System.IO.Path.ChangeExtension(input, ".labels.csv")), port);
    }
}

public static class LabelerApp
{
    // Empty builder: no production startup, application configuration, user secrets, or ambient URL binding.
    public static WebApplication Create(LabelerOptions options, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        if (options.Port is < 1024 or > 65535) throw new LabelerException("cli-port");
        var store = new LabelStore(Dataset.Load(options.Input), options.Labels);
        try
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(LabelerApp).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = "Production"
            });
            builder.WebHost.UseKestrel(k => k.ListenLocalhost(options.Port));
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddAntiforgery(a =>
            {
                a.Cookie.Name = "EvaluationLabeler.Antiforgery";
                a.Cookie.HttpOnly = true;
                a.Cookie.SameSite = SameSiteMode.Strict;
                a.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });
            builder.Services.AddRazorPages().AddApplicationPart(typeof(LabelerApp).Assembly);
            builder.Services.AddSingleton(_ => store);
            configureBuilder?.Invoke(builder);
            var app = builder.Build();
            _ = app.Services.GetRequiredService<LabelStore>(); // Container owns the store's lifetime.
            app.Use(async (context, next) =>
            {
                var request = context.Request;
                var host = request.Host.Host;
                var allowedHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || host == "127.0.0.1" || host == "[::1]";
                if (!allowedHost || request.Host.Port != options.Port
                    || (context.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip)))
                {
                    context.Response.StatusCode = 400;
                    return;
                }
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                if (HttpMethods.IsPost(request.Method)
                    && (!string.Equals(request.Headers.Origin, request.Scheme + "://" + request.Host.Value, StringComparison.OrdinalIgnoreCase)
                        || request.Headers["Sec-Fetch-Site"] == "cross-site"))
                {
                    context.Response.StatusCode = 403;
                    return;
                }
                try { await next(context); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = 503;
                        await context.Response.WriteAsJsonAsync(new { code = "io-failed", message = "Storage unavailable. Reload before retrying." });
                    }
                }
            });
            foreach (var name in new[] { "labeler.js", "labeler.css" })
            {
                using var stream = typeof(LabelerApp).Assembly.GetManifestResourceStream("Labeler." + name)!;
                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                app.MapGet("/" + name, () => Results.Text(content, name.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript" : "text/css"));
            }
            app.MapRazorPages();
            return app;
        }
        catch { store.Dispose(); throw; }
    }
}
