using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Handshake;

namespace RecipesManage.Infrastructure.Plc;

/// <summary>
/// OPC UA 握手驱动：点表地址为 NodeId（如 ns=2;s=Handshake.PLC_Ready）。
/// 与 S7/Modbus 共用同一套上位机变量，禁止绕过四步闭环盲写。
/// 安全默认：不自动接受未信任证书、匿名身份；点表可打开签名端点与用户名令牌。
/// </summary>
public sealed class OpcUaHandshakeClient : MappedPlcHandshakeClient
{
    private readonly string _endpoint;
    private ISession? _session;
    private ApplicationConfiguration? _config;

    public OpcUaHandshakeClient(EquipmentLine equipment) : base(equipment)
    {
        _endpoint = equipment.Host.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase)
            ? equipment.Host
            : $"opc.tcp://{equipment.Host}:{equipment.Port}";
    }

    public override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _config = BuildConfig();
        await _config.ValidateAsync(ApplicationType.Client).ConfigureAwait(false);
        _config.CertificateValidator.CertificateValidation += (_, e) =>
        {
            if (Map.OpcUaAutoAcceptCertificates)
                e.Accept = true;
        };
        var app = new ApplicationInstance
        {
            ApplicationName = _config.ApplicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationConfiguration = _config
        };
        await app.CheckApplicationInstanceCertificatesAsync(true, null, cancellationToken).ConfigureAwait(false);

        var selected = await CoreClientUtils.SelectEndpointAsync(
                _config, _endpoint, useSecurity: Map.OpcUaUseSecurity, cancellationToken)
            .ConfigureAwait(false);
        var configured = new ConfiguredEndpoint(null, selected, EndpointConfiguration.Create(_config));
        _session = await DefaultSessionFactory.Instance.CreateAsync(
            _config,
            configured,
            updateBeforeConnect: false,
            checkDomain: false,
            sessionName: "BRMES-Handshake",
            sessionTimeout: 60_000,
            identity: BuildIdentity(),
            preferredLocales: null,
            ct: cancellationToken).ConfigureAwait(false);
    }

    public override async Task<PlcInboundSignals> ReadSignalsAsync(CancellationToken cancellationToken)
    {
        var session = Require();
        return new PlcInboundSignals(
            await ReadBoolAsync(session, Map.PlcReady, cancellationToken).ConfigureAwait(false),
            await ReadBoolAsync(session, Map.StepRunning, cancellationToken).ConfigureAwait(false),
            await ReadBoolAsync(session, Map.StepComplete, cancellationToken).ConfigureAwait(false),
            await ReadBoolAsync(session, Map.StepError, cancellationToken).ConfigureAwait(false),
            await ReadIntAsync(session, Map.ErrorCode, cancellationToken).ConfigureAwait(false),
            (uint)Math.Max(0, await ReadIntAsync(session, Map.Heartbeat, cancellationToken).ConfigureAwait(false)),
            await ReadBoolAsync(session, Map.TriggerWrite, cancellationToken).ConfigureAwait(false),
            await ReadOptionalBoolAsync(session, Map.PlcHeld, cancellationToken).ConfigureAwait(false),
            await ReadOptionalBoolAsync(session, Map.HostHold, cancellationToken).ConfigureAwait(false));
    }

    public override async Task WriteStepPayloadAsync(int stepId, int stepType, IReadOnlyList<float> parameters, CancellationToken cancellationToken)
    {
        var session = Require();
        await WriteAsync(session, Map.StepId, stepId, cancellationToken).ConfigureAwait(false);
        await WriteAsync(session, Map.StepType, stepType, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < Map.Params.Count && i < 16; i++)
            await WriteAsync(session, Map.Params[i], i < parameters.Count ? parameters[i] : 0f, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<PlcStepPayload> ReadStepPayloadAsync(CancellationToken cancellationToken)
    {
        var session = Require();
        var count = Math.Min(Map.Params.Count, 16);
        var values = new float[count];
        for (var i = 0; i < count; i++)
            values[i] = await ReadFloatAsync(session, Map.Params[i], cancellationToken).ConfigureAwait(false);
        return new PlcStepPayload(
            await ReadIntAsync(session, Map.StepId, cancellationToken).ConfigureAwait(false),
            await ReadIntAsync(session, Map.StepType, cancellationToken).ConfigureAwait(false),
            values);
    }

    public override Task SetTriggerWriteAsync(bool value, CancellationToken cancellationToken) =>
        WriteAsync(Require(), Map.TriggerWrite, value, cancellationToken);

    public override Task SetHostHoldAsync(bool value, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(Map.HostHold)
            ? Task.CompletedTask
            : WriteAsync(Require(), Map.HostHold, value, cancellationToken);

    public override async Task ResetCompleteAsync(CancellationToken cancellationToken)
    {
        var session = Require();
        await WriteAsync(session, Map.StepComplete, false, cancellationToken).ConfigureAwait(false);
        await WriteAsync(session, Map.StepError, false, cancellationToken).ConfigureAwait(false);
        await WriteAsync(session, Map.ErrorCode, 0, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyDictionary<string, double>> ReadMeasuredAsync(CancellationToken cancellationToken)
    {
        var session = Require();
        var values = new Dictionary<string, double>();
        foreach (var (name, address) in Map.Measured)
            values[name] = Convert.ToDouble(await ReadValueAsync(session, address, cancellationToken).ConfigureAwait(false));
        return values;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_session is null)
            return;
        try
        {
            await _session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // closing a dropped session is best-effort
        }
        _session.Dispose();
        _session = null;
    }

    private IUserIdentity BuildIdentity()
    {
        if (string.IsNullOrWhiteSpace(Map.OpcUaUser))
            return new UserIdentity(new AnonymousIdentityToken());
        return new UserIdentity(Map.OpcUaUser.Trim(), Map.OpcUaPassword ?? "");
    }

    private ISession Require() =>
        _session ?? throw new InvalidOperationException("OPC UA 会话未连接。");

    private static async Task<bool> ReadBoolAsync(ISession session, string node, CancellationToken ct) =>
        Convert.ToBoolean(await ReadValueAsync(session, node, ct).ConfigureAwait(false));

    private static async Task<bool> ReadOptionalBoolAsync(ISession session, string? node, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(node) ? false : await ReadBoolAsync(session, node, ct).ConfigureAwait(false);

    private static async Task<float> ReadFloatAsync(ISession session, string node, CancellationToken ct) =>
        Convert.ToSingle(await ReadValueAsync(session, node, ct).ConfigureAwait(false));

    private static async Task<int> ReadIntAsync(ISession session, string node, CancellationToken ct) =>
        Convert.ToInt32(await ReadValueAsync(session, node, ct).ConfigureAwait(false));

    private static async Task<object> ReadValueAsync(ISession session, string node, CancellationToken ct)
    {
        var value = await session.ReadValueAsync(Resolve(session, node), ct).ConfigureAwait(false);
        if (StatusCode.IsBad(value.StatusCode))
            throw new InvalidOperationException($"OPC UA 读失败 {node}: {value.StatusCode}");
        return value.Value ?? 0;
    }

    private static async Task WriteAsync(ISession session, string node, object value, CancellationToken ct)
    {
        var collection = new WriteValueCollection
        {
            new WriteValue
            {
                NodeId = Resolve(session, node),
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(value))
            }
        };
        var response = await session.WriteAsync(null, collection, ct).ConfigureAwait(false);
        if (response.Results is { Count: > 0 } && StatusCode.IsBad(response.Results[0]))
            throw new InvalidOperationException($"OPC UA 写失败 {node}: {response.Results[0]}");
    }

    private static NodeId Resolve(ISession session, string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException("OPC UA 点表地址为空。");
        if (address.Contains("nsu=", StringComparison.OrdinalIgnoreCase) ||
            address.Contains("svr=", StringComparison.OrdinalIgnoreCase))
        {
            var expanded = ExpandedNodeId.Parse(address);
            return ExpandedNodeId.ToNodeId(expanded, session.NamespaceUris);
        }

        return NodeId.Parse(address);
    }

    private ApplicationConfiguration BuildConfig() => new()
    {
        ApplicationName = "RecipesManage",
        ApplicationUri = "urn:RecipesManage:BRMES",
        ApplicationType = ApplicationType.Client,
        SecurityConfiguration = new SecurityConfiguration
        {
            ApplicationCertificate = new CertificateIdentifier
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(ClientPkiRoot, "own"),
                SubjectName = "CN=RecipesManage BRMES Client"
            },
            TrustedIssuerCertificates = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(ClientPkiRoot, "issuer")
            },
            TrustedPeerCertificates = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(ClientPkiRoot, "trusted")
            },
            RejectedCertificateStore = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(ClientPkiRoot, "rejected")
            },
            AutoAcceptUntrustedCertificates = Map.OpcUaAutoAcceptCertificates,
            AddAppCertToTrustedStore = true,
            RejectSHA1SignedCertificates = false,
            MinimumCertificateKeySize = 1024
        },
        TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
        ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
    };

    private static string ClientPkiRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BRMES", "opcua-client-pki");
}
