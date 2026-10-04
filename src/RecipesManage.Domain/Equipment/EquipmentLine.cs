using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Equipment;

public enum PlcProtocol
{
    Simulator = 0,
    SiemensS7 = 1,
    ModbusTcp = 2,
    OpcUa = 3
}

public sealed class EquipmentLine : Entity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public PlcProtocol Protocol { get; private set; }
    public string Host { get; private set; } = "127.0.0.1";
    public int Port { get; private set; } = 102;
    public string PlcModel { get; private set; } = "S7_1200";
    public int Rack { get; private set; }
    public int Slot { get; private set; } = 1;
    public bool Enabled { get; private set; } = true;
    public string TagMapJson { get; private set; } = "{}";
    public string? WatchdogJson { get; private set; }
    public string? Description { get; private set; }
    public string? EquipmentClassCode { get; private set; }

    private EquipmentLine() { }

    public EquipmentLine(
        string code,
        string name,
        PlcProtocol protocol,
        string host,
        int port,
        string plcModel,
        int rack,
        int slot,
        string tagMapJson,
        string? description,
        string? watchdogJson = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new DomainException("EQ_CODE_REQUIRED", "设备编码不能为空。");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("EQ_NAME_REQUIRED", "设备名称不能为空。");

        Code = code.Trim().ToUpperInvariant();
        Name = name.Trim();
        Protocol = protocol;
        Host = host;
        Port = port;
        PlcModel = plcModel;
        Rack = rack;
        Slot = slot;
        TagMapJson = tagMapJson;
        WatchdogJson = watchdogJson;
        Description = description;
    }

    public void Update(
        string name,
        PlcProtocol protocol,
        string host,
        int port,
        string plcModel,
        int rack,
        int slot,
        bool enabled,
        string tagMapJson,
        string? description,
        string? watchdogJson = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("EQ_NAME_REQUIRED", "设备名称不能为空。");

        Name = name.Trim();
        Protocol = protocol;
        Host = host;
        Port = port;
        PlcModel = plcModel;
        Rack = rack;
        Slot = slot;
        Enabled = enabled;
        TagMapJson = tagMapJson;
        WatchdogJson = watchdogJson;
        Description = description;
        Touch();
    }

    public void AssignClass(string? equipmentClassCode)
    {
        EquipmentClassCode = string.IsNullOrWhiteSpace(equipmentClassCode)
            ? null
            : equipmentClassCode.Trim().ToUpperInvariant();
        Touch();
    }

    public void ReplaceTagMap(string tagMapJson)
    {
        TagMapJson = tagMapJson;
        Touch();
    }
}

public sealed class HandshakeTagMap
{
    public string StepId { get; set; } = "DB10.0";
    public string StepType { get; set; } = "DB10.4";
    public string TriggerWrite { get; set; } = "DB10.8.0";
    public string PlcReady { get; set; } = "DB10.8.1";
    public string StepRunning { get; set; } = "DB10.8.2";
    public string StepComplete { get; set; } = "DB10.8.3";
    public string StepError { get; set; } = "DB10.8.4";
    public string HostHold { get; set; } = "DB10.8.5";
    public string PlcHeld { get; set; } = "DB10.8.6";
    public string ErrorCode { get; set; } = "DB10.12";
    public string Heartbeat { get; set; } = "DB10.16";
    public List<string> Params { get; set; } = CreateDefaultParams();
    public Dictionary<string, string> Measured { get; set; } = new()
    {
        ["Temperature"] = "DB10.84",
        ["Pressure"] = "DB10.88",
        ["HoldTime"] = "DB10.92"
    };

    /// <summary>OPC UA：使用签名端点。默认关闭以便实验室明文联调。</summary>
    public bool OpcUaUseSecurity { get; set; }

    /// <summary>OPC UA：自动接受未信任证书。生产必须关闭。</summary>
    public bool OpcUaAutoAcceptCertificates { get; set; }

    public string? OpcUaUser { get; set; }
    public string? OpcUaPassword { get; set; }

    /// <summary>
    /// IOTClient Modbus TCP 点表：线圈 0–4 为握手位，保持寄存器承载 Step_ID / 参数 / 实测。
    /// 与 S7 DB 地址空间分离，禁止混用。
    /// </summary>
    public const string OpcUaNamespaceUri = "urn:brmes:handshake";

    /// <summary>
    /// OPC UA 环回点表：命名空间 URI 固定，避免 NamespaceIndex 随服务器加载顺序漂移。
    /// 实验室默认匿名 + 接受自签证书；生产必须关闭 AutoAccept 并启用签名端点。
    /// </summary>
    public static HandshakeTagMap OpcUaLoopback() => new()
    {
        StepId = OpcNode("Step_ID"),
        StepType = OpcNode("Step_Type"),
        TriggerWrite = OpcNode("Trigger_Write"),
        PlcReady = OpcNode("PLC_Ready"),
        StepRunning = OpcNode("Step_Running"),
        StepComplete = OpcNode("Step_Complete"),
        StepError = OpcNode("Step_Error"),
        HostHold = OpcNode("Host_Hold"),
        PlcHeld = OpcNode("PLC_Held"),
        ErrorCode = OpcNode("Error_Code"),
        Heartbeat = OpcNode("Heartbeat"),
        Params = Enumerable.Range(0, 16).Select(i => OpcNode($"Param_{i}")).ToList(),
        Measured = new Dictionary<string, string>
        {
            ["Temperature"] = OpcNode("Temperature"),
            ["Pressure"] = OpcNode("Pressure"),
            ["HoldTime"] = OpcNode("HoldTime")
        },
        OpcUaUseSecurity = false,
        OpcUaAutoAcceptCertificates = true
    };

    public static string OpcNode(string name) => $"nsu={OpcUaNamespaceUri};s={name}";

    public static HandshakeTagMap ModbusLoopback() => new()
    {
        StepId = "0",
        StepType = "2",
        TriggerWrite = "0",
        PlcReady = "1",
        StepRunning = "2",
        StepComplete = "3",
        StepError = "4",
        HostHold = "5",
        PlcHeld = "7",
        ErrorCode = "4",
        Heartbeat = "6",
        Params = Enumerable.Range(0, 16).Select(i => $"{10 + i * 2}").ToList(),
        Measured = new Dictionary<string, string>
        {
            ["Temperature"] = "50",
            ["Pressure"] = "52",
            ["HoldTime"] = "54"
        }
    };

    private static List<string> CreateDefaultParams()
    {
        var list = new List<string>(16);
        for (var i = 0; i < 16; i++)
            list.Add($"DB10.{20 + i * 4}");
        return list;
    }
}
