using Opc.Ua;
using Opc.Ua.Server;
using RecipesManage.Domain.Equipment;

namespace RecipesManage.Infrastructure.Plc;

internal sealed class HandshakeNodeManager : CustomNodeManager2
{
    private readonly SimulatedPlcStation _station;
    private readonly Dictionary<string, BaseDataVariableState> _nodes = new(StringComparer.Ordinal);
    private readonly BaseDataVariableState[] _params = new BaseDataVariableState[16];

    public HandshakeNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration,
        SimulatedPlcStation station)
        : base(server, configuration, HandshakeTagMap.OpcUaNamespaceUri)
    {
        _station = station;
        SystemContext.NodeIdFactory = this;
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
            {
                references = new List<IReference>();
                externalReferences[ObjectIds.ObjectsFolder] = references;
            }

            var root = CreateFolder(null, "Handshake", "Handshake");
            root.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, root.NodeId));
            root.EventNotifier = EventNotifiers.SubscribeToEvents;
            AddRootNotifier(root);

            Bool(root, "PLC_Ready", true);
            Bool(root, "Step_Running", false);
            Bool(root, "Step_Complete", false);
            Bool(root, "Step_Error", false);
            Bool(root, "Trigger_Write", false);
            Bool(root, "Host_Hold", false);
            Bool(root, "PLC_Held", false);
            Int(root, "Step_ID", 0);
            Int(root, "Step_Type", 0);
            Int(root, "Error_Code", 0);
            Int(root, "Heartbeat", 0);
            Float(root, "Temperature", 25f);
            Float(root, "Pressure", 1.01f);
            Float(root, "HoldTime", 0f);
            for (var i = 0; i < 16; i++)
                _params[i] = Float(root, $"Param_{i}", 0f);

            AddPredefinedNode(SystemContext, root);
            PublishFromStation();
        }
    }

    public void PublishFromStation()
    {
        var signals = _station.ReadSignals();
        var measured = _station.ReadMeasured();
        lock (Lock)
        {
            Set("PLC_Ready", signals.PlcReady);
            Set("Step_Running", signals.StepRunning);
            Set("Step_Complete", signals.StepComplete);
            Set("Step_Error", signals.StepError);
            Set("Trigger_Write", signals.TriggerWriteEcho);
            Set("Host_Hold", signals.HostHoldEcho);
            Set("PLC_Held", signals.PlcHeld);
            Set("Error_Code", signals.ErrorCode);
            Set("Heartbeat", (int)signals.Heartbeat);
            if (measured.TryGetValue("Temperature", out var t))
                Set("Temperature", (float)t);
            if (measured.TryGetValue("Pressure", out var p))
                Set("Pressure", (float)p);
            if (measured.TryGetValue("HoldTime", out var h))
                Set("HoldTime", (float)h);
        }
    }

    private void ApplyWrite(string name, object value)
    {
        if (name == "Host_Hold")
        {
            _station.SetHostHold(Convert.ToBoolean(value));
            PublishFromStation();
            return;
        }

        if (name == "Trigger_Write")
        {
            var on = Convert.ToBoolean(value);
            if (on)
            {
                var parameters = new float[16];
                for (var i = 0; i < 16; i++)
                    parameters[i] = Convert.ToSingle(_params[i].Value ?? 0f);
                var stepId = Convert.ToInt32(_nodes["Step_ID"].Value ?? 0);
                var stepType = Convert.ToInt32(_nodes["Step_Type"].Value ?? 0);
                _station.WritePayload(stepId, stepType, parameters);
                _station.SetTrigger(true);
            }
            else
            {
                _station.SetTrigger(false);
            }

            PublishFromStation();
            return;
        }

        if ((name is "Step_Complete" or "Step_Error") && !Convert.ToBoolean(value))
        {
            _station.ResetComplete();
            PublishFromStation();
        }
    }

    private FolderState CreateFolder(NodeState? parent, string path, string name)
    {
        var folder = new FolderState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            NodeId = new NodeId(path, NamespaceIndex),
            BrowseName = new QualifiedName(path, NamespaceIndex),
            DisplayName = new LocalizedText("en", name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            EventNotifier = EventNotifiers.None
        };
        parent?.AddChild(folder);
        return folder;
    }

    private BaseDataVariableState Bool(NodeState parent, string name, bool value) =>
        CreateVariable(parent, name, DataTypeIds.Boolean, value);

    private BaseDataVariableState Int(NodeState parent, string name, int value) =>
        CreateVariable(parent, name, DataTypeIds.Int32, value);

    private BaseDataVariableState Float(NodeState parent, string name, float value) =>
        CreateVariable(parent, name, DataTypeIds.Float, value);

    private BaseDataVariableState CreateVariable(NodeState parent, string name, NodeId dataType, object value)
    {
        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId(name, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText("en", name),
            WriteMask = AttributeWriteMask.DisplayName | AttributeWriteMask.Description,
            UserWriteMask = AttributeWriteMask.DisplayName | AttributeWriteMask.Description,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentReadOrWrite,
            UserAccessLevel = AccessLevels.CurrentReadOrWrite,
            Historizing = false,
            Value = value,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow
        };
        variable.OnWriteValue = OnWrite;
        variable.OnSimpleReadValue = OnRead;
        parent.AddChild(variable);
        _nodes[name] = variable;
        return variable;
    }

    private ServiceResult OnRead(ISystemContext context, NodeState node, ref object value)
    {
        PublishFromStation();
        if (node is BaseDataVariableState variable)
            value = variable.Value;
        return ServiceResult.Good;
    }

    private ServiceResult OnWrite(
        ISystemContext context,
        NodeState node,
        NumericRange indexRange,
        QualifiedName dataEncoding,
        ref object value,
        ref StatusCode statusCode,
        ref DateTime timestamp)
    {
        if (node is not BaseDataVariableState variable)
            return StatusCodes.BadNodeIdUnknown;

        variable.Value = value;
        variable.Timestamp = DateTime.UtcNow;
        variable.StatusCode = StatusCodes.Good;
        timestamp = variable.Timestamp;
        statusCode = StatusCodes.Good;
        if (variable.NodeId.Identifier is string name)
            ApplyWrite(name, value);
        return ServiceResult.Good;
    }

    private void Set(string name, object value)
    {
        if (!_nodes.TryGetValue(name, out var node))
            return;
        node.Value = value;
        node.Timestamp = DateTime.UtcNow;
        node.StatusCode = StatusCodes.Good;
        node.ClearChangeMasks(SystemContext, false);
    }
}
