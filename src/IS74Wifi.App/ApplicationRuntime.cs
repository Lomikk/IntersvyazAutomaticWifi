using IS74Wifi.Core;

namespace IS74Wifi.App;

internal sealed class ApplicationRuntime : IDisposable
{
    private readonly HttpClient apiHttp;
    private readonly HttpClient portalHttp;
    private readonly HttpClient internetHttp;

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
        HttpClient apiHttp,
        HttpClient portalHttp,
        HttpClient internetHttp)
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
        this.apiHttp = apiHttp;
        this.portalHttp = portalHttp;
        this.internetHttp = internetHttp;
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

    public static ApplicationRuntime Create()
    {
        var paths = new AppPaths();
        var json = new JsonFileStore();
        var settings = new SettingsStore(paths, json).Load();
        var logger = new DiagnosticLogger(paths);
        var secrets = new DpapiSecretStore(paths);
        var deviceIdentity = new DeviceIdentityStore(paths);
        var session = new SessionMetadataStore(paths, json);
        var runtimeState = new RuntimeStateStore(paths, json);
        var authorizationState = new AuthorizationStateManager(runtimeState, settings);

        var apiHttp = HttpClientProfiles.CreateApiClient();
        var portalHttp = HttpClientProfiles.CreatePortalClient();
        var internetHttp = HttpClientProfiles.CreateInternetProbeClient();
        var api = new Is74ApiClient(new HttpTransport(apiHttp));
        var portal = new CaptivePortalClient(new HttpTransport(portalHttp));
        var internet = new InternetConnectivityProbe(new HttpTransport(internetHttp));
        var wifi = new WindowsWifiEnvironment();
        var polling = new PushPollingEngine(api);
        var authorization = new AuthorizationFlow(
            api,
            portal,
            internet,
            wifi,
            polling,
            authorizationState,
            logger);
        var agent = new AgentService(
            secrets,
            deviceIdentity,
            authorizationState,
            authorization,
            internet,
            wifi,
            settings,
            logger);

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
            apiHttp,
            portalHttp,
            internetHttp);
    }

    public void Dispose()
    {
        apiHttp.Dispose();
        portalHttp.Dispose();
        internetHttp.Dispose();
    }
}
