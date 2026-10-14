using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Kugutsu.Hosting;

namespace Kugutsu.App;

/// <summary>An expected HTTP startup failure without raw transport exception text.</summary>
internal sealed class HttpHostException(LogError reason) : Exception("HTTP host startup failed.")
{
    public LogError Reason { get; } = reason;
}

internal sealed class HttpHostShutdownException(LogError reason) : Exception("HTTP host shutdown failed.")
{
    public LogError Reason { get; } = reason;
}

/// <summary>Composes the loopback-only MCP host inside the executable.</summary>
internal static class HttpHost
{
    private static readonly TimeSpan LifecycleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Creates the host without opening sockets. Only settings control binding.</summary>
    public static WebApplication Create(KugutsuOptions settings, InstallationPaths paths,
        ILoggerProvider logProvider, BearerCredential credential)
    {
        var endpoint = GetEndpoint(settings.ServerUrl);
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = paths.RootDirectory, EnvironmentName = Environments.Production,
            ApplicationName = typeof(Program).Assembly.GetName().Name
        });
        builder.Host.UseConsoleLifetime();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(new BorrowedLogProvider(logProvider));
        builder.Services.AddRouting();
        builder.Services.AddMetrics();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(_ => new LogicalSessions(settings.MaxClientSessions));
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<WindowReferenceRegistry>();
        builder.Services.AddSingleton<WindowCoordinates>();
        builder.Services.AddSingleton<WindowDiscovery>();
        builder.Services.AddSingleton<WindowStateOperations>();
        builder.Services.AddSingleton<MouseOperations>();
        builder.Services.AddSingleton<KeyboardOperations>();
        builder.Services.AddSingleton<ScreenCapture>();
        builder.Services.AddSingleton<ILocalOcr, TesseractLocalOcr>();
        builder.Services.AddSingleton<VisualShapeDetector>();
        builder.Services.AddSingleton<VisualLabelAssociator>();
        builder.Services.AddSingleton<VisualProfileCatalog>();
        builder.Services.AddSingleton<VisualScreenMatcher>();
        builder.Services.AddSingleton<VisualMeaningInference>();
        builder.Services.AddSingleton<VisualFrameRegistry>();
        builder.Services.AddSingleton<VisualReferenceValidator>();
        builder.Services.AddSingleton<VisualFocusVerifier>();
        builder.Services.AddSingleton<VisualControlActions>();
        builder.Services.AddSingleton<VisualValueReader>();
        builder.Services.AddSingleton<AloeDependencies>();
        builder.Services.AddSingleton<AloeCompilerAdapter>();
        builder.Services.AddSingleton<AloeRuntimeAdapter>();
        builder.Services.AddSingleton<AloeOperationBridge>();
        builder.Services.AddSingleton<AloeBatchRunner>();
        builder.Services.AddSingleton<VisualAnalysisPipeline>();
        builder.Services.AddSingleton<IUiDriver, ImGuiVisualDriver>();
        builder.Services.AddSingleton<IUiDriver, GenericVisualDriver>();
        builder.Services.AddSingleton<IUiDriver, UiaDriver>();
        builder.Services.AddSingleton<Func<UiElementRegistry>>(services => () => services.GetRequiredService<UiElementRegistry>());
        builder.Services.AddSingleton<UiFindOperation>();
        builder.Services.AddSingleton<UiSnapshotOperation>();
        builder.Services.AddSingleton<UiControlOperations>();
        builder.Services.AddSingleton<LaunchTargetRegistry>();
        builder.Services.AddSingleton<ApplicationReferenceRegistry>();
        builder.Services.AddSingleton<ApplicationOperations>();
        builder.Services.AddSingleton<WindowOpenOperation>();
        builder.Services.AddSingleton<ApplicationExitOperation>();
        builder.Services.AddSingleton<ShellSessionRegistry>();
        builder.Services.AddSingleton<ManagedProcessTree>();
        builder.Services.AddSingleton<TerminalLaunchOperation>();
        builder.Services.AddSingleton<ShellInputOperations>();
        builder.Services.AddSingleton<ShellExitOperations>();
        builder.Services.AddSingleton<ConsoleAssociationProbe>();
        builder.Services.AddSingleton<PowerShellLaunchOperation>();
        builder.Services.AddSingleton<BrowserDefaults>();
        builder.Services.AddSingleton<BrowserDiscovery>();
        builder.Services.AddSingleton<BrowserSessionRegistry>();
        builder.Services.AddSingleton(services =>
        {
            var driverSettings = new KugutsuOptions { AllowedLaunchTargets = settings.BrowserDriverPaths.Select(pair =>
                new LaunchTargetOptions { Id = pair.Key, ExecutablePath = pair.Value }).ToArray() };
            var driverTargets = new LaunchTargetRegistry(driverSettings);
            return new BrowserDriverFiles(driverTargets, new ApplicationOperations(driverTargets,
                services.GetRequiredService<ApplicationReferenceRegistry>()));
        });
        builder.Services.AddSingleton<BrowserLaunchOperation>();
        builder.Services.AddSingleton<BrowserDriverVersion>();
        builder.Services.AddSingleton<BrowserNavigationOperations>();
        builder.Services.AddSingleton<BrowserOperationGate>();
        builder.Services.AddSingleton<BrowserExitOperation>();
        builder.Services.AddSingleton<DomElementRegistry>();
        builder.Services.AddSingleton<DomOperations>();
        builder.Services.AddSingleton<SignalRegistry>();
        builder.Services.AddSingleton<ISignalConditionProvider, WindowStateSignalProvider>();
        builder.Services.AddSingleton<ISignalConditionProvider, WindowOpenedSignalProvider>();
        builder.Services.AddSingleton<ISignalConditionProvider, ControlStateSignalProvider>();
        builder.Services.AddSingleton<ISignalConditionProvider, ControlTextSignalProvider>();
        builder.Services.AddSingleton<ISignalConditionProvider, DomStateSignalProvider>();
        builder.Services.AddSingleton<ISignalConditionProvider, DomTextSignalProvider>();
        builder.Services.AddSingleton<ISignalConditionProvider, BrowserUrlSignalProvider>();
        builder.Services.AddSingleton<SignalRegistration>();
        builder.Services.AddHostedService<SignalLifetimeService>();
        builder.Services.AddSingleton<SignalWaiting>();
        builder.Services.AddSingleton<ExecuteOperationTools>();
        builder.Services.AddSingleton<UiDriverRegistry>();
        builder.Services.AddSingleton<UiElementRegistry>();
        builder.Services.AddSingleton(services => new OperationDispatcher([
            services.GetRequiredService<WindowDiscovery>().ListDefinition(),
            services.GetRequiredService<WindowDiscovery>().GetDefinition(),
            services.GetRequiredService<WindowStateOperations>().RestoreDefinition(),
            services.GetRequiredService<WindowStateOperations>().ActivateDefinition(),
            services.GetRequiredService<WindowStateOperations>().MinimizeDefinition(),
            services.GetRequiredService<WindowStateOperations>().MaximizeDefinition(),
            services.GetRequiredService<WindowStateOperations>().CloseDefinition(),
            services.GetRequiredService<MouseOperations>().ClickDefinition(),
            services.GetRequiredService<MouseOperations>().DoubleClickDefinition(),
            services.GetRequiredService<MouseOperations>().ScrollDefinition(),
            services.GetRequiredService<KeyboardOperations>().PressKeyDefinition(),
            services.GetRequiredService<KeyboardOperations>().TypeTextDefinition(),
            WaitOperation.Definition(),
            services.GetRequiredService<ScreenCapture>().Definition(),
            services.GetRequiredService<ScreenCapture>().DesktopDefinition(),
            services.GetRequiredService<UiDriverRegistry>().ListDefinition(),
            services.GetRequiredService<UiFindOperation>().Definition(),
            services.GetRequiredService<UiSnapshotOperation>().Definition(),
            services.GetRequiredService<UiControlOperations>().StateDefinition(),
            services.GetRequiredService<UiControlOperations>().ValueDefinition(),
            services.GetRequiredService<UiControlOperations>().InvokeDefinition(),
            services.GetRequiredService<UiControlOperations>().SetValueDefinition(),
            services.GetRequiredService<UiControlOperations>().SendKeysDefinition(),
            services.GetRequiredService<UiControlOperations>().SelectDefinition(),
            services.GetRequiredService<UiControlOperations>().ToggleDefinition(),
            services.GetRequiredService<UiControlOperations>().RangeDefinition(),
            services.GetRequiredService<UiControlOperations>().ExpandDefinition(),
            services.GetRequiredService<UiControlOperations>().ScrollDefinition(),
            services.GetRequiredService<UiControlOperations>().SelectItemsDefinition(),
            services.GetRequiredService<UiControlOperations>().TextSelectionDefinition(),
            services.GetRequiredService<UiControlOperations>().GridCellDefinition(),
            services.GetRequiredService<UiControlOperations>().GridSetValueDefinition(),
            services.GetRequiredService<ApplicationOperations>().LaunchDefinition(),
            services.GetRequiredService<WindowOpenOperation>().Definition(),
            services.GetRequiredService<ApplicationExitOperation>().Definition(),
            services.GetRequiredService<TerminalLaunchOperation>().Definition(),
            services.GetRequiredService<ShellInputOperations>().TerminalDefinition(),
            services.GetRequiredService<ShellExitOperations>().TerminalDefinition(),
            services.GetRequiredService<PowerShellLaunchOperation>().Definition(),
            services.GetRequiredService<ShellInputOperations>().PowerShellDefinition(),
            services.GetRequiredService<ShellExitOperations>().PowerShellDefinition(),
            services.GetRequiredService<BrowserDefaults>().Definition(),
            services.GetRequiredService<BrowserDiscovery>().Definition(),
            services.GetRequiredService<BrowserLaunchOperation>().Definition(),
            services.GetRequiredService<BrowserNavigationOperations>().NavigateDefinition(),
            services.GetRequiredService<BrowserNavigationOperations>().BackDefinition(),
            services.GetRequiredService<BrowserNavigationOperations>().ForwardDefinition(),
            services.GetRequiredService<BrowserExitOperation>().Definition(),
            services.GetRequiredService<DomOperations>().FindDefinition(),
            services.GetRequiredService<DomOperations>().FillDefinition(),
            services.GetRequiredService<DomOperations>().ClickDefinition(),
            services.GetRequiredService<DomOperations>().KeysDefinition(),
            services.GetRequiredService<DomOperations>().ValueDefinition(),
            services.GetRequiredService<SignalRegistration>().Definition(),
            services.GetRequiredService<SignalWaiting>().Definition(),
            services.GetRequiredService<SignalRegistration>().UnregisterDefinition()],
            services.GetRequiredService<BrowserOperationGate>()));
        builder.Services.AddSingleton(serviceProvider => new OperationRequestQueue(settings.QueueCapacity,
            settings.QueueTimeoutSeconds,
            settings.OperationTimeoutSeconds,
            settings.RequestTimeoutSeconds,
            settings.ResultRetentionMinutes,
            serviceProvider.GetRequiredService<ILogger<OperationRequestQueue>>()));
        builder.Services.Configure<HostOptions>(options =>
        {
            options.StartupTimeout = LifecycleTimeout;
            options.ShutdownTimeout = LifecycleTimeout;
        });
        builder.WebHost.UseKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = settings.MaxRequestBytes;
            if (endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            { options.ListenLocalhost(endpoint.Port); }
            else
            { options.Listen(IPAddress.Parse(endpoint.Host.Trim('[', ']')), endpoint.Port); }
        });
        builder.Services.AddMcpServer(options => options.ServerInfo = new()
        {
            Name = ProductInformation.Name, Version = ProductInformation.Version
        }).WithHttpTransport().WithTools<VersionTools>().WithTools<ExecuteOperationTools>()
            .WithTools<GetRequestTools>().WithTools<CancelRequestTools>().WithTools<LockMcpTools>()
            .WithTools<WindowTools>().WithTools<SignalTools>();
        var app = builder.Build();
        var sessions = app.Services.GetRequiredService<LogicalSessions>();
        var issuancePath = SessionProtocol.IssuanceEndpoint(endpoint).AbsolutePath;
        var resumePath = SessionProtocol.ResumeEndpoint(endpoint).AbsolutePath;
        app.Use((HttpContext context, RequestDelegate next) =>
        {
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
                || !ValidHost(context.Request.Host, endpoint) || !ValidOrigin(context.Request, endpoint))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            if (!BearerAuthentication.IsAuthorized(context.Request, credential))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                context.Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            }
            return next(context);
        });
        app.Use((HttpContext context, RequestDelegate next) =>
        {
            // 接続確認は所有者を必要としない。発行・再開にも共有Bearerの検証を適用済み。
            if (HttpMethods.IsOptions(context.Request.Method)
                || HttpMethods.IsPost(context.Request.Method)
                    && (string.Equals(context.Request.Path.Value, issuancePath, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(context.Request.Path.Value, resumePath, StringComparison.OrdinalIgnoreCase)))
            { return next(context); }
            if (!sessions.Authenticate(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            }
            return next(context);
        });
        app.MapPost(issuancePath, async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.ContentLength != 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")
                || context.Request.Headers.ContainsKey(SessionProtocol.IdHeader)
                || context.Request.Headers.ContainsKey(SessionProtocol.CredentialHeader)
                || context.Request.Headers.ContainsKey(SessionProtocol.ResumeHeader))
            { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            var grant = sessions.Issue();
            if (grant is null)
            { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsJsonAsync(grant, context.RequestAborted);
        });
        app.MapPost(resumePath, async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.ContentLength != 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")
                || context.Request.Headers.ContainsKey(SessionProtocol.CredentialHeader))
            { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            if (!sessions.Resume(context))
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            await context.Response.WriteAsJsonAsync(new
            { sessionId = LogicalSessions.GetOwner(context).Id.ToString("N"), resumed = true }, context.RequestAborted);
        });
        app.MapMcp(endpoint.AbsolutePath);
        return app;
    }

    /// <summary>Starts the listener with a bounded wait and port-conflict classification.</summary>
    public static async Task StartAsync(WebApplication host, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LifecycleTimeout);
        try { await host.StartAsync(timeout.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or SocketException
            or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        {
            var reason = LogError.HttpHostStartFailed;
            for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
            {
                if (cause is AddressInUseException || cause is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                { reason = LogError.HttpPortInUse; break; }
            }
            throw new HttpHostException(reason);
        }
    }

    internal static async Task StopAsync(WebApplication host)
    {
        using var timeout = new CancellationTokenSource(LifecycleTimeout);
        try { await host.StopAsync(timeout.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or SocketException
            or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        { throw new HttpHostShutdownException(LogError.HttpHostStopFailed); }
    }

    private static Uri GetEndpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttp || endpoint.Port <= 0
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || !(endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address))
            || !endpoint.AbsolutePath.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '/' or '-' or '.' or '_' or '~'))
        { throw new FormatException("Invalid loopback HTTP endpoint."); }
        return endpoint;
    }

    private static bool ValidHost(HostString host, Uri endpoint) =>
        (host.Port ?? 80) == endpoint.Port
        && host.Host.Trim('[', ']').Equals(endpoint.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);

    private static bool ValidOrigin(HttpRequest request, Uri endpoint)
    {
        if (!request.Headers.TryGetValue("Origin", out var origins)) { return true; }
        return origins.Count == 1 && Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
            && origin.Scheme == endpoint.Scheme && origin.Port == endpoint.Port
            && origin.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase)
            && origin.UserInfo.Length == 0 && origin.AbsolutePath == "/"
            && origin.Query.Length == 0 && origin.Fragment.Length == 0;
    }

    /// <summary>The executable owns the audit provider after the HTTP host is disposed.</summary>
    private sealed class BorrowedLogProvider(ILoggerProvider provider) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);
        public void Dispose() { /* The outer executable owns the provider. */ }
    }
}
