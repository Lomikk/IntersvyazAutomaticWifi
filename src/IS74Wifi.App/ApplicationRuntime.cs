using IS74Wifi.Core;

namespace IS74Wifi.App;

internal sealed class ApplicationRuntime : IDisposable
{
    private const string DefaultTelemetryEndpoint =
        "https://script.google.com/macros/s/AKfycbw9JLeOD1hhtQPf3zm91XdnntODEUBMYbJzmO-SMwzhPRbRy3kDcTXZP0bg97sKQl-0bA/exec";

    private readonly HttpClient apiHttp;
    private readonly HttpClient portalHttp;
    private readonly HttpClient internetHttp;
    private readonly HttpClient speedTestHttp;
    private readonly HttpClient? telemetryHttp;

    private ApplicationRuntime(
        AppPaths paths,
        JsonFileStore json,
        AppSettings settings,
        DiagnosticLogger logger,
        DpapiSecretStore secrets,
        DeviceIdentityStore deviceIdentity,
        SessionMetadataStore session,
        RuntimeStateStore runtimeState,
        AuthorizationStateManager authorizationState,
        Is74ApiClient api,
        InternetConnectivityProbe internet,
        AuthorizationFlow authorization,
        AgentService agent,
        WindowsAutostartService autostart,
        LocalStateMaintenance maintenance,
        TelemetryQueue telemetryQueue,
        TelemetryUploader telemetryUploader,
        CampusSpeedToolsService campusSpeedTools,
        HttpClient apiHttp,
        HttpClient portalHttp,
        HttpClient internetHttp,
        HttpClient speedTestHttp,
        HttpClient? telemetryHttp)
    {
        Paths = paths;
        Json = json;
        Settings = settings;
        Logger = logger;
        Secrets = secrets;
        DeviceIdentity = deviceIdentity;
        Session = session;
        RuntimeState = runtimeState;
        AuthorizationState = authorizationState;
        Api = api;
        Internet = internet;
        Authorization = authorization;
        Agent = agent;
        Autostart = autostart;
        Maintenance = maintenance;
        TelemetryQueue = telemetryQueue;
        TelemetryUploader = telemetryUploader;
        CampusSpeedTools = campusSpeedTools;
        this.apiHttp = apiHttp;
        this.portalHttp = portalHttp;
        this.internetHttp = internetHttp;
        this.speedTestHttp = speedTestHttp;
        this.telemetryHttp = telemetryHttp;
    }

    public AppPaths Paths { get; }
    public JsonFileStore Json { get; }
    public AppSettings Settings { get; }
    public DiagnosticLogger Logger { get; }
    public DpapiSecretStore Secrets { get; }
    public DeviceIdentityStore DeviceIdentity { get; }
    public SessionMetadataStore Session { get; }
    public RuntimeStateStore RuntimeState { get; }
    public AuthorizationStateManager AuthorizationState { get; }
    public Is74ApiClient Api { get; }
    public InternetConnectivityProbe Internet { get; }
    public AuthorizationFlow Authorization { get; }
    public AgentService Agent { get; }
    public WindowsAutostartService Autostart { get; }
    public LocalStateMaintenance Maintenance { get; }
    public TelemetryQueue TelemetryQueue { get; }
    public TelemetryUploader TelemetryUploader { get; }
    public CampusSpeedToolsService CampusSpeedTools { get; }

    public static ApplicationRuntime Create(string appVersion = "dev")
    {
        var paths = new AppPaths();
        var json = new JsonFileStore();
        var settingsStore = new SettingsStore(paths, json);
        var settings = settingsStore.Load();
        var logger = new DiagnosticLogger(paths);
        var secrets = new DpapiSecretStore(paths);
        var deviceIdentity = new DeviceIdentityStore(paths);
        var session = new SessionMetadataStore(paths, json);
        var runtimeState = new RuntimeStateStore(paths, json);
        var authorizationState = new AuthorizationStateManager(runtimeState, settings);

        // Production uses the ordinary system resolver directly. Cached/direct-IP
        // connection experiments must not add hidden latency before DNS.
        var apiHttp = HttpClientProfiles.CreateApiClient();
        var portalHttp = HttpClientProfiles.CreatePortalClient();
        var internetHttp = HttpClientProfiles.CreateInternetProbeClient();
        var speedTestHttp = HttpClientProfiles.CreateSpeedTestClient();
        var api = new Is74ApiClient(new HttpTransport(apiHttp));
        var portal = new CaptivePortalClient(new HttpTransport(portalHttp));
        var internet = new InternetConnectivityProbe(new HttpTransport(internetHttp));
        var wifi = new WindowsWifiEnvironment();
        var polling = new PushPollingEngine(api);

        // Authorization telemetry is opt-in and local-first. When allowed, the
        // critical path only mutates in-memory trace state; durable queue writes
        // happen after RunAsync returns.
        var telemetryIdentity = new TelemetryIdentityStore(paths);
        var telemetryInstallId = telemetryIdentity.GetOrCreate();
        var telemetryQueue = new TelemetryQueue(paths);
        bool AnonymousStatisticsAllowed() =>
            settingsStore.Load().AnonymousStatisticsConsent == AnonymousStatisticsConsent.Allowed;
        var telemetryRecorder = settings.AnonymousStatisticsConsent == AnonymousStatisticsConsent.Allowed
            ? new AuthorizationTelemetryRecorder(telemetryInstallId, telemetryQueue, appVersion)
            : null;

        var telemetryEndpoint = ResolveTelemetryEndpoint(settings);
        HttpClient? telemetryHttp = null;
        TelemetryClient? telemetryClient = null;
        if (telemetryEndpoint is not null)
        {
            telemetryHttp = HttpClientProfiles.CreateTelemetryClient();
            telemetryClient = new TelemetryClient(telemetryHttp, telemetryEndpoint, logger);
        }
        var telemetryUploader = new TelemetryUploader(
            telemetryQueue,
            new TelemetryUploadStateStore(paths, json),
            telemetryClient,
            settings,
            logger,
            AnonymousStatisticsAllowed);
        var campusSpeedTools = new CampusSpeedToolsService(
            new Is74SpeedTestProvider(speedTestHttp),
            telemetryClient,
            telemetryQueue,
            telemetryInstallId,
            appVersion,
            settings.InteractiveBackendTimeoutMilliseconds,
            AnonymousStatisticsAllowed);

        var authorization = new AuthorizationFlow(
            api,
            portal,
            internet,
            wifi,
            polling,
            authorizationState,
            logger,
            telemetry: telemetryRecorder,
            ignoreNetworkCheck: settings.IgnoreNetworkCheck);
        var notifications = new WindowsNotificationService(settingsStore, logger);
        var agent = new AgentService(
            secrets,
            deviceIdentity,
            authorizationState,
            authorization,
            internet,
            wifi,
            settings,
            logger,
            notifications: notifications);

        return new ApplicationRuntime(
            paths,
            json,
            settings,
            logger,
            secrets,
            deviceIdentity,
            session,
            runtimeState,
            authorizationState,
            api,
            internet,
            authorization,
            agent,
            new WindowsAutostartService(),
            new LocalStateMaintenance(paths),
            telemetryQueue,
            telemetryUploader,
            campusSpeedTools,
            apiHttp,
            portalHttp,
            internetHttp,
            speedTestHttp,
            telemetryHttp);
    }

    internal static Uri ResolveTelemetryEndpoint(AppSettings settings)
    {
        var configured = Environment.GetEnvironmentVariable("IS74W_TELEMETRY_URL");
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = settings.TelemetryEndpoint;
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = DefaultTelemetryEndpoint;
        }

        return Uri.TryCreate(configured, UriKind.Absolute, out var endpoint) &&
               endpoint.Scheme == Uri.UriSchemeHttps
            ? endpoint
            : new Uri(DefaultTelemetryEndpoint, UriKind.Absolute);
    }

    public void Dispose()
    {
        apiHttp.Dispose();
        portalHttp.Dispose();
        internetHttp.Dispose();
        speedTestHttp.Dispose();
        telemetryHttp?.Dispose();
    }
}
