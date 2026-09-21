using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;
using RecipesManage.Domain.Equipment;

namespace RecipesManage.Infrastructure.Plc;

/// <summary>
/// 本机 OPC UA 握手从站：把 OPC Foundation 会话读写映射到 <see cref="SimulatedPlcStation"/>。
/// 与 Modbus 环回相同，仅用于协议栈联调，不替代现场 PLC。
/// </summary>
public sealed class OpcUaHandshakeSlave : IAsyncDisposable
{
    public const string PathSuffix = "/BRMES";
    public SimulatedPlcStation Station { get; } = new();
    public HandshakeTagMap Map { get; } = HandshakeTagMap.OpcUaLoopback();
    public int Port { get; private set; }
    public string Endpoint { get; private set; } = "";
    public Guid? BoundEquipmentId { get; private set; }

    private HandshakeOpcServer? _server;
    private ApplicationInstance? _app;
    private Timer? _publish;

    public void BindEquipment(Guid equipmentId) => BoundEquipmentId = equipmentId;

    public bool IsBoundTo(Guid equipmentId) => BoundEquipmentId == equipmentId;

    public async Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        Port = port == 0 ? FreePort() : port;
        Endpoint = $"opc.tcp://127.0.0.1:{Port}{PathSuffix}";
        var pki = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BRMES", "opcua-pki");
        Directory.CreateDirectory(pki);

        var config = BuildServerConfig(Endpoint, pki);
        await config.ValidateAsync(ApplicationType.Server).ConfigureAwait(false);
        config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = true;

        _app = new ApplicationInstance
        {
            ApplicationName = config.ApplicationName,
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = config
        };
        var ok = await _app.CheckApplicationInstanceCertificatesAsync(true, null, cancellationToken)
            .ConfigureAwait(false);
        if (!ok)
            throw new InvalidOperationException("OPC UA 环回从站无法创建应用证书。");

        cancellationToken.ThrowIfCancellationRequested();
        _server = new HandshakeOpcServer(Station);
        await _app.StartAsync(_server).ConfigureAwait(false);
        _publish = new Timer(_ =>
        {
            try { _server.NodeManager?.PublishFromStation(); }
            catch { /* best-effort */ }
        }, null, 40, 40);
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _publish?.Dispose();
        _publish = null;
        try { _app?.Stop(); }
        catch { /* shutdown */ }
        _app = null;
        _server = null;

        await Task.CompletedTask;
    }

    private static ApplicationConfiguration BuildServerConfig(string endpoint, string pki) => new()
    {
        ApplicationName = "BRMES OPC UA Handshake Loopback",
        ApplicationUri = $"urn:localhost:BRMES:OpcUaLoopback",
        ProductUri = "urn:brmes:opcua-loopback",
        ApplicationType = ApplicationType.Server,
        SecurityConfiguration = new SecurityConfiguration
        {
            ApplicationCertificate = new CertificateIdentifier
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(pki, "own"),
                SubjectName = "CN=BRMES OPC UA Loopback, DC=localhost"
            },
            TrustedIssuerCertificates = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(pki, "issuer")
            },
            TrustedPeerCertificates = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(pki, "trusted")
            },
            RejectedCertificateStore = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(pki, "rejected")
            },
            AutoAcceptUntrustedCertificates = true,
            AddAppCertToTrustedStore = true,
            RejectSHA1SignedCertificates = false,
            MinimumCertificateKeySize = 1024
        },
        TransportQuotas = new TransportQuotas { OperationTimeout = 15000, MaxStringLength = 1_048_576 },
        ServerConfiguration = new ServerConfiguration
        {
            BaseAddresses = { endpoint },
            MinRequestThreadCount = 2,
            MaxRequestThreadCount = 8,
            MaxQueuedRequestCount = 20,
            SecurityPolicies =
            {
                new ServerSecurityPolicy
                {
                    SecurityMode = MessageSecurityMode.None,
                    SecurityPolicyUri = SecurityPolicies.None
                }
            },
            UserTokenPolicies = { new UserTokenPolicy(UserTokenType.Anonymous) },
            DiagnosticsEnabled = false,
            MaxSessionCount = 16,
            MinSessionTimeout = 10_000,
            MaxSessionTimeout = 60_000,
            MaxBrowseContinuationPoints = 10,
            MaxQueryContinuationPoints = 10,
            MaxHistoryContinuationPoints = 10,
            MaxRequestAge = 600_000,
            MinPublishingInterval = 50,
            MaxPublishingInterval = 3_600_000,
            PublishingResolution = 50,
            MaxSubscriptionLifetime = 3_600_000,
            MaxMessageQueueSize = 10,
            MaxNotificationQueueSize = 100,
            MaxNotificationsPerPublish = 1000,
            MaxPublishRequestCount = 20,
            MaxSubscriptionCount = 50
        },
        DisableHiResClock = true
    };

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal sealed class HandshakeOpcServer : StandardServer
{
    private readonly SimulatedPlcStation _station;

    public HandshakeOpcServer(SimulatedPlcStation station) => _station = station;

    public HandshakeNodeManager? NodeManager { get; private set; }

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration)
    {
        NodeManager = new HandshakeNodeManager(server, configuration, _station);
        return new MasterNodeManager(server, configuration, null, NodeManager);
    }

    protected override ServerProperties LoadServerProperties() => new()
    {
        ManufacturerName = "BRMES",
        ProductName = "OPC UA Handshake Loopback",
        ProductUri = "urn:brmes:opcua-loopback",
        SoftwareVersion = "1.0",
        BuildNumber = "1",
        BuildDate = DateTime.UtcNow
    };
}
