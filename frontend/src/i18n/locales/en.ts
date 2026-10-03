/**
 * 英文文案表。键 = 界面里的中文原文（见 ../index.ts 的理由说明）。
 *
 * 三条约束由 i18n/coverage.spec 守着，别靠记性：
 *   - 代码里每个 $t("中文") 都必须在这里有一行（缺译只是显示中文，但棘轮基线不许涨）；
 *   - 带 {0} 占位的键必须登记（中文身份表只收带占位的键，漏了界面会直接露 "{0}"）；
 *   - 这里也不留已经没人用的死键。
 * 不译的东西：用户数据（配方名、设备名、审批链节点名）、审计与电子签名原文——
 * 那些是落库事实，读取时再翻译会让同一份批记录在不同人屏幕上不一致。
 *
 * 少数条目是**句子碎片**（如以「。 」或「： 」开头）：模板里它们紧跟一个 {{ 表达式 }}，
 * 中文抽取只能从中间断开。译文按"接在前半句后面"来写，不是独立成句。
 */
const en: Record<string, string> = {
  // ---- 顶栏、侧栏与路由标题（router 是唯一名单，不另建第二份） ----
  "运行总览": "Overview",
  "主配方设计": "Master recipes",
  "多级审核": "Approvals",
  "审批链配置": "Approval chains",
  "批次执行": "Batch execution",
  "物料谱系": "Material genealogy",
  "过程报警": "Process alarms",
  "设备 / PLC": "Equipment / PLC",
  "操作审计": "Audit trail",
  "用户与备份": "Users & backup",
  "快捷键": "Shortcuts",
  "退出": "Sign out",
  "界面语言": "Language",
  "配方设计态": "Recipe design",
  "配方设计": "Recipe designer",
  "批次执行态": "Batch execution",
  "批次追溯记录": "Batch record",
  "批次实时监控": "Live batch monitor",
  "物料批次": "Material lots",
  "设备与驱动": "Equipment & drivers",
  "页面不存在": "Page not found",

  // ---- 登录与外壳 ----
  "登录": "Sign in",
  "用户名": "Username",
  "密码": "Password",
  "关闭菜单": "Close menu",
  "打开菜单": "Open menu",
  "工艺配方管理与实时执行": "Recipe management & live execution",
  "工艺配方管理与实时执行系统": "Recipe Management & Batch Execution",
  "离散制造 · 批次配方管理与执行": "Discrete manufacturing · batch recipes and execution",
  "演示账号 · 点一下填入用户名与密码": "Demo accounts · click to fill in user and password",
  "登录状态已失效，请重新登录后继续操作。": "Your session has expired. Please sign in again to continue.",

  // ---- 列表计数（带占位，中文身份表就靠这一类键） ----
  "共 {0} 条": "{0} items",

  // ---- 设备表单 / 握手点表 ----
  "实时工艺趋势": "Live process trend",
  "核对地址": "Check addresses",
  "基本与超时": "Basics & timeouts",
  "编码": "Code",
  "名称": "Name",
  "协议": "Protocol",
  "主机": "Host",
  "IP 或 opc.tcp://host:4840": "IP or opc.tcp://host:4840",
  "端口": "Port",
  "型号": "Model",
  "机架 / 插槽": "Rack / slot",
  "启用": "Enabled",
  "设备类": "Equipment class",
  "未分类则跳过相能力校验": "Leave uncategorised to skip phase-capability checks",
  "握手看门狗（秒）": "Handshake watchdog (seconds)",
  "等待 Ready": "Ready wait",
  "写参超时": "Write timeout",
  "应答超时": "Ack timeout",
  "心跳超时": "Heartbeat timeout",
  "复位超时": "Reset timeout",
  "保持应答": "Hold ack",
  "握手点表": "Handshake tag map",
  "实测点": "Measured point",
  "配方参数按这里的键名读实测值归档": "Recipe parameters read their archived measured value from the key defined here",
  "。 键名要与配方里填的一致；粘度、流量、计数这类非热工量自己加行，不要挤进 Temperature。":
    ". The key must match what the recipe declares; add a row for non-thermal quantities such as viscosity or flow instead of reusing Temperature.",
  "键名，如 Viscosity": "Key, e.g. Viscosity",
  "地址，如 DB40.0": "Address, e.g. DB40.0",
  "删除": "Delete",
  "这台设备没有实测点。配方里声明了实测点的参数会在开批时被拦下。":
    "This device has no measured points. Parameters that declare one will block batch start.",
  "添加实测点": "Add measured point",
  "OPC UA 安全（默认不自动接受证书、匿名）": "OPC UA security (certificates not auto-accepted, anonymous by default)",
  "签名端点": "Signed endpoint",
  "接受自签证书": "Accept self-signed certificates",
  "留空为匿名": "Leave blank for anonymous",
  "写参槽": "Parameter slots",
  "工步设定值按槽位写入这些地址。默认 DB10.20 起每 4 字节一槽，与现场点表不符时必须改，否则写参会落到错误偏移。":
    "Step setpoints are written to these addresses by slot. The default starts at DB10.20 with one slot every 4 bytes; change it when the plant tag map differs, otherwise writes land at the wrong offset.",
  "关闭": "Close",
  "保存": "Save",
  "本车道握手信号当前全为 0（批次未启动或已完成）":
    "All handshake signals on this lane are currently 0 (batch not started, or already finished)",

  // ---- 并行单元 / 相库 / 相模板 ----
  "新增并行单元规程": "Add parallel unit procedure",
  "新单元与现有工步无前驱连线，可绑定不同 PLC 并行四步握手。编排完后用下方「添加连线」汇合到质检工步。":
    "The new unit has no predecessor links, so it can bind a different PLC and run the four-step handshake in parallel. Use “Add link” below to join it into the quality step.",
  "单元规程": "Unit procedure",
  "首工步相": "First step phase",
  "当前设备类没有相模板。": "The current equipment class has no phase templates.",
  "去相库添加": "Add one in the phase library",
  "取消": "Cancel",
  "添加并行工步": "Add parallel step",
  "写 PLC 的相模板按设备类集中维护；程序号给设计器和握手 Step_Type 用。等待 / 质检 / 人工确认不进本库。":
    "Templates that write to the PLC are maintained per equipment class; the program number feeds the designer and the handshake Step_Type. Wait / quality / manual-confirm steps do not belong here.",
  "当前类": "Current class",
  "该类还没有相模板": "This class has no templates yet",
  "执行类": "Execution kind",
  "程序号": "Program no.",
  "操作": "Action",
  "看门狗": "Watchdog",
  "参数槽": "Parameter slot",
  "编辑": "Edit",
  "新增相模板": "Add phase template",
  "没有可维护的设备类：先建设备类，才能给它加相模板。":
    "No equipment class to maintain: create one before adding phase templates to it.",
  "如 PH-SPRAY": "e.g. PH-SPRAY",
  "类型别名": "Type alias",
  "如 OP-Rinse 水冲洗": "e.g. OP-Rinse water rinse",
  "看门狗（秒）": "Watchdog (s)",
  "槽": "Slot",
  "参数": "Parameter",
  "设定值": "Setpoint",
  "下限": "Min",
  "上限": "Max",
  "单位": "Unit",
  "留空按名称推断": "Leave blank to infer from the name",
  "实测值必须填：点表 Measured 的键名": "Required for a measured value: the key in the point table's Measured map",
  "新增参数槽": "Add parameter slot",
  "删除末槽": "Remove last slot",

  // ---- 写参计划 / 版本差异 ----
  "工步": "Step",
  "写参": "Write",
  "策略": "Policy",
  "待执行": "Pending",
  "执行中": "Running",
  "待确认/保持": "Awaiting / held",
  "完成": "Completed",
  "跳过": "Skipped",
  "故障": "Fault",
  "相对生效版新增": "Added vs released",
  "参数有改": "Parameters changed",
  "控制配方快照尚无工步": "The control recipe snapshot has no steps",
  "版本差异": "Version diff",
  "两个版本工艺内容相同": "Both versions have identical procedure content",
  "路径": "Path",
  "之前": "Before",
  "之后": "After",

  // ---- 配方抬头 ----
  "主配方抬头": "Master recipe header",
  "产品编码": "Product code",
  "产品名称": "Product name",
  "说明": "Description",
  "保存抬头": "Save header",
  "尚无工步参数可组成设定矩阵": "No step parameters to build a setpoint matrix from yet",

  // ---- 报警 ----
  "握手故障、质检超差和调度异常。确认后仍保留，供放行与追溯。":
    "Handshake faults, quality excursions and scheduling anomalies. Acknowledged entries stay visible for release and traceability.",
  "搜索批次 / 代码 / 说明": "Search batch / code / description",
  "未确认": "Unacked",
  "全部": "All",
  "时间": "Time",
  "批次": "Batch",
  "代码": "Code",
  "级别": "Severity",
  "确认": "Acknowledge",

  // ---- 审计 ----
  "配方、批次、设备与账号的关键操作留痕，可按实体还原一次闭环。":
    "Key operations on recipes, batches, equipment and accounts, reconstructable per entity.",
  "刷新": "Refresh",
  "搜索用户 / 动作 / 详情": "Search user / action / detail",
  "全部实体": "All entities",
  "主配方": "Master recipe",
  "生产批次": "Production batch",
  "设备": "Equipment",
  "相模板": "Phase template",
  "用户": "User",
  "物料批": "Material lot",
  "实验室样品": "Lab sample",
  "暂无符合条件的审计记录": "No audit records match the current filters",
  "动作": "Action",
  "实体": "Entity",
  "详情": "Detail",

  // ---- 批次列表与创建 ----
  "从已批准主配方生成控制配方，并跟踪执行到放行。":
    "Generate a control recipe from an approved master recipe and follow it through to release.",
  "从已批准配方创建": "Create from approved recipe",
  "搜索批次号 / 配方 / 设备": "Search batch no. / recipe / equipment",
  "已创建": "Created",
  "排队": "Queued",
  "保持": "Hold",
  "待放行": "Awaiting release",
  "待检终样": "Final sample pending",
  "已放行": "Released",
  "已拒收": "Rejected",
  "已中止": "Aborted",
  "仅显示有待检终样的批次。打开电子批记录判定样品。":
    "Showing only batches with a pending final sample. Judge them in the electronic batch record.",
  "批次号": "Batch no.",
  "控制配方": "Control recipe",
  "版本": "Version",
  "主设备": "Primary equipment",
  "状态": "Status",
  "创建时间": "Created",
  "创建批次 / 生成控制配方快照": "Create batch / freeze control recipe",
  "选择已批准主配方": "Select an approved master recipe",
  "没有已批准的主配方。草稿不会出现在此列表，请先走审核批准。":
    "No approved master recipe. Drafts are not listed here — take one through approval first.",
  "选择主设备": "Select primary equipment",
  "单元设备": "Unit equipment",
  "同一波次的单元规程绑定不同设备时并行四步握手；同一设备仍串行。":
    "Unit procedures in the same wave run the four-step handshake in parallel when bound to different equipment; one device still runs serially.",
  "共用同一台设备，这一波会退回串行执行（一台设备同时只允许一个四步握手）。":
    " share one device, so this wave falls back to serial execution (a device allows only one four-step handshake at a time).",
  "缩放因子": "Scale factor",
  "产出批号，写入控制配方快照（不纳入哈希）":
    "Output lot number, stored in the control recipe snapshot (excluded from the hash)",
  "投料批": "Input lots",
  "可选，来料/拆分子批": "Optional, incoming or split child lots",
  "生成快照并创建": "Freeze snapshot and create",
  "请确认该批次是否存在，或返回批次列表重试。": "Check that this batch exists, or go back to the batch list and try again.",

  // ---- 批次监控 ----
  "人工确认": "Manual confirm",
  "恢复执行": "Resume",
  "跳过当前工步": "Skip current step",
  "中止": "Abort",
  "返回批次列表": "Back to batches",
  "电子批记录": "Electronic batch record",
  "工艺执行与四步握手已完成。请质量在电子批记录对照归档质检后电子签名放行。":
    "Execution and the four-step handshake are complete. Quality should compare the archived results in the electronic batch record, then sign to release.",
  "等待人工确认": "Awaiting manual confirmation",
  "本工步禁止写 PLC。操作员 / 质量电子签名确认后才进入下一步。":
    "This step must not write to the PLC. The next step starts only after an operator or quality e-signature.",
  "批次仍在运行。调度会写 Host_Hold，待 PLC_Held 后再暂停；刷新或重启不会丢掉这条已签名指令。":
    "The batch is still running. The scheduler writes Host_Hold and pauses once PLC_Held is seen; refreshing or restarting does not lose this signed instruction.",
  "等待当前工步回到可跳相位后执行，重启后仍会继续。":
    "Waiting for the current step to return to a skippable phase; this survives a restart.",
  "人工确认已提交": "Manual confirmation submitted",
  "查看报警": "View alarms",
  "控制配方快照 · 工艺画布（冻结拓扑，禁止盲写）":
    "Control recipe snapshot · procedure canvas (frozen topology, no blind writes)",
  "与设计态同一套工艺画布。坐标按快照连线自动排布（不写入完整性哈希）。点击工步可筛选质检。":
    "The same canvas as design mode. Coordinates are laid out from the snapshot links (not part of the integrity hash). Click a step to filter quality results.",
  "冻结设定矩阵（快照 vs 归档实测）": "Frozen setpoint matrix (snapshot vs archived actuals)",
  "批次创建时从生效主配方冻结，执行中禁止改写。工步完成握手步骤 D 后，单元格显示归档实测并对照规格判定超差。":
    "Frozen from the released master recipe at batch creation and immutable during execution. After handshake step D, cells show the archived actual and flag excursions against the spec.",
  "PLC 写参计划（与调度引擎同源 · 拓扑顺序）": "PLC write plan (same source as the scheduler · topology order)",
  "启动前即可核对 Step_ID / Params。Wait、人工确认、质检工步禁止写 PLC；写参工步仅在 PLC_Ready 后下发并回读。":
    "Verify Step_ID / Params before start. Wait, manual-confirm and quality steps never write to the PLC; write steps send only after PLC_Ready and read back.",
  "ISA-88 工步（按单元规程）": "ISA-88 steps (by unit procedure)",
  "工步归档质检": "Step archived quality results",
  "测点": "Tag",
  "实测": "Actual",
  "规格": "Spec",
  "完成握手步骤 D 后，此处显示该工步归档实测。点击时间线工步可筛选。":
    "After handshake step D the archived actuals for this step appear here. Click a step in the timeline to filter.",
  "趋势": "Trend",
  "报警": "Alarms",
  "本批无过程报警。": "No process alarms for this batch.",
  "履历": "History",
  "握手时序（禁止盲写）": "Handshake sequence (no blind writes)",
  "阶段": "Phase",
  "剩余s": "Remaining s",
  "启动批次后，此处按 PLC_Ready → Trigger_Write → Step_Running → Step_Complete 记录每一次合法动作。":
    "Once the batch starts, every permitted action is recorded here in PLC_Ready → Trigger_Write → Step_Running → Step_Complete order.",
  "快照 vs 当前生效主配方": "Snapshot vs current released master recipe",
  "控制配方快照已冻结。主配方升版只体现在漂移列，禁止改写本批次写参。":
    "The control recipe snapshot is frozen. Later revisions show only in the drift column and cannot change this batch's writes.",
  "快照": "Snapshot",
  "漂移": "Drift",

  // ---- 实时状态与总览 ----
  "服务正常": "Service healthy",
  "服务异常": "Service degraded",
  "更新于 {0}": "updated {0}",
  "停在 {0}": "stopped at {0}",
  "时长": "Duration",
  "最新未确认报警": "Latest unconfirmed alarm",
  "去处理": "Fix now",
  "质量超差": "Quality deviation",
  "最久积压 {0}": "oldest waiting {0}",
  "只看非空闲": "Busy only",
  "最近 2 小时没有批次事件": "No batch events in the last 2 hours",
  "第 {0} 步": "Step {0}",
  "需处理": "Needs attention",
  "参考": "Reference",
  "API 健康检查失败": "API health check failed",
  "后端 /health 未返回 ok，运行总览与实时推送可能均已中断。":
    "The backend /health did not return ok; overview and live updates may both be interrupted.",
  "执行态势": "Execution status",
  "设备占用（一台设备同时只允许一个运行/排队/保持批次）":
    "Equipment occupancy (one running/queued/held batch per device at a time)",
  "暂无设备数据": "No equipment data",
  "批次状态": "Batch status",
  "在途批次（运行 / 排队 / 保持 / 故障）": "In-flight batches (running / queued / held / faulted)",
  "当前没有在途批次": "No batches in flight",
  "配方": "Recipe",

  // ---- 设备列表 ----
  "设备与 PLC 驱动": "Equipment & PLC drivers",
  "产线设备、连接参数，以及设备类上的相模板。":
    "Plant equipment, connection parameters, and the phase templates on each equipment class.",
  "新增设备": "Add equipment",
  "占用": "Busy",
  "空闲": "Idle",
  "测试连接": "Test connection",
  "校验点表": "Validate tag map",
  "仿真故障": "Inject fault",
  "驱动层解耦：Simulator / Siemens S7（IoTClient） / Modbus TCP（IoTClient） / OPC UA（OPC Foundation）。握手变量集合固定，禁止绕过 PLC_Ready 盲写。":
    "Drivers are decoupled: Simulator / Siemens S7 (IoTClient) / Modbus TCP (IoTClient) / OPC UA (OPC Foundation). The handshake variable set is fixed, and writing without PLC_Ready is forbidden.",
  "相库": "Phase library",

  // ---- 404 ----
  "没有找到地址": "No page matches the address",
  "对应的页面，可能是链接已过时或地址输入有误。": " — the link may be outdated, or the address was mistyped.",
  "回到运行总览": "Back to overview",
  "返回上一页": "Go back",
  "请确认该批次是否存在，或返回列表重试。": "Check that this batch exists, or return to the list and try again.",
  "返回列表": "Back to list",

  // ---- 物料谱系 ----
  "拆分子批": "Split child lot",
  "祖先批": "Ancestor lots",
  "来料根批，无祖先。": "Incoming root lot, no ancestors.",
  "本批": "This lot",
  "子批": "Child lots",
  "尚未拆分子批。": "No child lots split yet.",
  "关联生产批次（投料 / 产出）": "Linked production batches (input / output)",
  "本批未关联生产批次": "This lot is not linked to any production batch",
  "生产批": "Batch",
  "角色": "Role",
  "物料": "Material",
  "数量": "Quantity",
  "子批号": "Child lot no.",
  "拆分": "Split",
  "物料批次谱系": "Material lot genealogy",
  "登记来料批": "Register incoming lot",
  "搜索批号 / 物料": "Search lot no. / material",
  "来料批可拆分成子批投料；生产批次号写入控制配方快照（不纳入完整性哈希）。实验室样品与 PLC 测点分开归档。":
    "Incoming lots can be split into child lots for dosing; production batch numbers are stored in the control recipe snapshot (excluded from the integrity hash). Lab samples are archived separately from PLC tags.",
  "来源": "Source",
  "物料编码": "Material code",
  "物料名称": "Material name",
  "登记": "Register",

  // ---- 审批链配置 ----
  "一条链 = 一串有序的「谁来签 · 签的时候看到哪句话」。配方可以各走各的链，没选的走默认链。":
    "A chain is an ordered list of who signs and which wording they see when signing. Recipes may each pick their own chain; those that do not pick one use the default.",
  "改链只影响之后的提交": "Editing a chain only affects later submissions",
  "：在审版本的节点在提交那刻已经冻结，改这里动不了它。":
    ": nodes of a version under review were already frozen at submission, so edits here cannot change them.",
  "新建链": "New chain",
  "已有的链": "Existing chains",
  "还没有审批链": "No approval chains yet",
  "链": "Chain",
  "默认": "Default",
  "停用": "Disabled",
  "选择左侧一条链，或点「新建链」": "Select a chain on the left, or choose “New chain”",
  "保存并电子签名": "Save with e-signature",
  "如 short-qa": "e.g. short-qa",
  "如 小变更短链": "e.g. Minor change short chain",
  "审核节点（自上而下依次签）": "Review nodes (signed top to bottom)",
  "节点名称": "Node name",
  "如 工艺主管": "e.g. Process supervisor",
  "要求角色": "Required role",
  "通过含义": "Approve meaning",
  "驳回含义": "Reject meaning",
  "顺序": "Order",
  "上移": "Move up",
  "下移": "Move down",
  "添加节点": "Add node",
  "同一角色在一条链里只能出现一次；提交配方的人（工艺工程师）与操作设备的人（操作员）不能当审核角色。 签名含义会随整条链冻结进审批记录，事后改这里不会影响已经提交的那一版。":
    "A role may appear only once per chain; the person who submits a recipe (Process engineer) and the person who operates equipment (Operator) cannot act as reviewers. Signature wording is frozen into the approval record with the whole chain, so later edits here do not affect an already-submitted version.",

  // ---- 审核台 ----
  "多级审核工作台": "Multi-level review",
  "按配方选定的审批链逐级签署；当前角色只能处理轮到自己的那一级。链由管理员在「审批链配置」里维护。":
    "Signatures follow the chain selected for the recipe, level by level; your role can only handle the level that is currently waiting for it. Chains are maintained by an administrator under “Approval chains”.",
  "搜索编码 / 名称 / 产品": "Search code / name / product",
  "待审核配方": "Recipes awaiting review",
  "待审节点": "Pending node",
  "审版": "Rev.",
  "选择左侧配方，在此审阅工艺与冻结设定矩阵": "Select a recipe on the left to review its procedure and frozen setpoint matrix",
  "通过并电子签名": "Approve with e-signature",
  "驳回": "Reject",
  "打开设计器": "Open designer",
  "本版本已无待审节点": "This version has no pending nodes",
  "生效版": "Released",
  "审核版": "Under review",
  "与生效版工艺内容相同": "Identical procedure content to the released version",
  "工艺工步": "Procedure steps",
  "参数矩阵（只读审阅）": "Parameter matrix (read-only review)",
  "电子签名链": "Electronic signature chain",
  "审核节点": "Review node",
  "结论": "Decision",
  "签署人": "Signed by",
  "签署含义": "Signature meaning",
  "签署时间": "Signed at",
  "意见": "Comment",
  "请确认该配方是否存在，或返回主配方列表重试。": "Check that this recipe exists, or go back to the master recipe list and try again.",

  // ---- 配方设计器 ----
  "· 未保存": " · unsaved",
  "返回配方列表": "Back to recipes",
  "编辑抬头": "Edit header",
  "ISA-88 泳道排布": "ISA-88 lane layout",
  "保存工艺": "Save procedure",
  "提交审核": "Submit for review",
  "通过": "Approve",
  "重新打开": "Reopen",
  "升版": "New version",
  "版本对比": "Compare versions",
  "工步列表": "Step list",
  "工艺工步编排（ISA-88 单元规程泳道）": "Procedure layout (ISA-88 unit procedure lanes)",
  "从相模板添加": "Add from template",
  "管理相库": "Manage phase library",
  "上位机": "Host-side",
  "+ 并行单元规程": "+ parallel unit procedure",
  "删除工步": "Delete step",
  "工艺连线：同一单元内串行；并行单元不要互连。跨单元只从末工步进入下一单元首工步（汇合质检）。":
    "Links: serial within one unit; do not cross-link parallel units. Across units, only the last step may enter the next unit's first step (joining at quality).",
  "删除连线": "Delete link",
  "前驱工步": "Predecessor",
  "后继 / 汇合": "Successor / join",
  "添加连线": "Add link",
  "汇合到当前工步": "Join into current step",
  "工步字段": "Step fields",
  "类型": "Type",
  "上位机工步，禁止写 PLC": "Host-side step, must not write to the PLC",
  "必填，如 UP-01 加工单元": "Required, e.g. UP-01 Machining cell",
  "必填，如 OP-Heat 升温": "Required, e.g. OP-Heat ramp up",
  "先在左侧工步列表或画布上选一个工步，这里会显示它的字段。":
    "Select a step in the list on the left or on the canvas to show its fields here.",
  "参数槽位": "Parameter slots",
  "未选择工步：参数槽位跟着工步走，先在左侧或画布上选一个。":
    "No step selected: parameter slots belong to a step, so pick one on the left or on the canvas first.",
  "控制参数矩阵（工步 × 设定值）": "Parameter matrix (steps × setpoints)",

  // ---- 配方列表 ----
  "维护主配方与单元规程。草稿提交后进入多级审核。":
    "Maintain master recipes and unit procedures. A submitted draft enters multi-level review.",
  "导出 JSON": "Export JSON",
  "导入 JSON": "Import JSON",
  "新建配方": "New recipe",
  "草稿": "Draft",
  "审核中": "In review",
  "已生效": "Released",
  "产品": "Product",
  "生效版本": "Released version",
  "草稿状态": "Draft status",
  "新建主配方": "New master recipe",
  "如 AL-HT-01": "e.g. AL-HT-01",
  "创建": "Create",

  // ---- 用户与备份 ----
  "账号与角色，以及数据库备份。": "Accounts and roles, plus database backup.",
  "下载 SQLite": "Download SQLite",
  "新建用户": "New user",
  "搜索登录名 / 显示名": "Search login name / display name",
  "登录名": "Login name",
  "显示名": "Display name",
  "新密码": "New password",
  "留空则不改": "Leave blank to keep unchanged",

  // ==== labels.ts 字典值（经 translate() 在运行时取用，源码里没有 $t 字样） ====
  // 握手相位 / 批次与工步状态
  "等待 PLC 就绪": "Waiting for PLC ready",
  "下发参数": "Writing parameters",
  "等待 PLC 应答": "Waiting for PLC ack",
  "工步执行中": "Step running",
  "收尾确认": "Completing",
  "可推进": "Ready to advance",
  "握手故障": "Handshake fault",
  "等待主控": "Waiting for host",
  "已暂停": "Paused",
  "排队中": "Queuing",
  "已排程": "Planned",
  "已保持": "Held",
  "已完成": "Completed",
  "已跳过": "Skipped",
  "四步握手": "Four-step handshake",
  // 角色
  "系统管理员": "Administrator",
  "工艺工程师": "Process engineer",
  "工艺主管": "Process supervisor",
  "质量工程师": "Quality engineer",
  "车间操作员": "Shop-floor operator",
  // 设备与协议
  "仿真器": "Simulator",
  // 物料批
  "在库": "In stock",
  "已消耗": "Consumed",
  "隔离": "Quarantine",
  "作废": "Void",
  "来料": "Received",
  "产出": "Produced",
  "投料": "Charged",
  "返工": "Rework",
  // 配方与审核
  "生效": "Released",
  "历史": "Superseded",
  "配方版本": "Recipe version",
  "提交": "Submission",
  "质量审核": "Quality review",
  "出厂放行": "Release",
  "待签署": "Awaiting signature",
  "已通过": "Approved",
  "已驳回": "Rejected",
  // 报警与样品
  "提示": "Info",
  "警告": "Warning",
  "严重": "Critical",
  "待判定": "Pending",
  "合格": "Pass",
  "不合格": "Fail",
  "过程样": "In-process sample",
  "终检样": "Final sample",
  "留样": "Retain sample",
  // 握手动作与工步类型
  "触发": "Trigger",
  "回读": "Verify",
  "等待": "Wait",
  "质检": "Quality check",
  "跳步": "Skip",
  "转阶段": "Phase change",
  "归档": "Archive",
  "复位": "Reset",
  "推进": "Advance",
  "恢复": "Resume",
  "升温": "Heat up",
  "保温": "Hold",
  "冷却": "Cool down",
  "搅拌": "Mix",
  "加压": "Pressurise",
  "转移": "Transfer",
  "写 PLC": "Write PLC",
  "未声明": "Unspecified",
  "工艺时长": "Duration",
  "速率": "Rate",
  "实测值": "Measured value",
  // 趋势图测点名（图例与轴名共用，别出现两种叫法）
  "温度": "Temperature",
  "压力": "Pressure",
  "保温时长": "Hold time",
  // 审计动作（操作审计页的动作列）
  "创建批次": "Batch created",
  "电子签名启动": "Started (e-signed)",
  "电子签名重新排队": "Re-queued (e-signed)",
  "电子签名中止": "Aborted (e-signed)",
  "电子签名保持": "Held (e-signed)",
  "电子签名恢复": "Resumed (e-signed)",
  "电子签名跳步": "Step skipped (e-signed)",
  "电子签名确认工步": "Step confirmed (e-signed)",
  "电子签名放行": "Released (e-signed)",
  "电子签名拒收": "Rejected (e-signed)",
  "导出电子批记录": "Batch record exported",
  "确认报警": "Alarm acknowledged",
  "创建配方": "Recipe created",
  "修改配方抬头": "Recipe header updated",
  "电子签名保存工艺": "Procedure saved (e-signed)",
  "电子签名提交审核": "Submitted for review (e-signed)",
  "电子签名审核": "Reviewed (e-signed)",
  "电子签名重新打开": "Reopened (e-signed)",
  "电子签名升版": "New version (e-signed)",
  "导入配方": "Recipe imported",
  "自动补升温时长": "Auto-filled heat duration",
  "自动修复乱码字段": "Auto-repaired mojibake fields",
  "自动补 ISA-88 单元": "Auto-filled ISA-88 units",
  "自动回填设备类": "Auto-backfilled equipment class",
  "自动继承设备类": "Auto-inherited equipment class",
  "自动补保持握手点表": "Auto-added hold handshake tags",
  "自动补相模板与程序号": "Auto-added phase templates and program ids",
  "创建设备": "Equipment created",
  "更新设备": "Equipment updated",
  "注入仿真故障": "Simulator fault injected",
  "更新相模板": "Phase template updated",
  "删除相模板": "Phase template deleted",
  "创建用户": "User created",
  "更新用户": "User updated",
  "登录成功": "Signed in",
  "登录失败": "Sign-in failed",
  "登录锁定": "Sign-in locked",
  "下载整库备份": "Full database backup downloaded",
  "来料登记": "Incoming lot registered",
  "登记样品": "Sample registered",
  "电子签名判定样品": "Sample judged (e-signed)",
  // 电子签名含义：签署时展示的原文。存进审计的仍是后端那份中文原文，
  // 这里只改"签的人看到什么语言"，不改"记录里留下什么"。
  "我作为操作员确认控制配方快照完整有效，启动本批四步握手，禁止盲写。":
    "As the operator I confirm the control recipe snapshot is complete and valid, and start this batch's four-step handshake; blind writes are forbidden.",
  "我作为操作员确认故障已排除，从当前工步重新排队并恢复握手。":
    "As the operator I confirm the fault is cleared and re-queue this batch from the current step, resuming the handshake.",
  "我作为操作员确认中止本批，停止写参并释放设备占用。":
    "As the operator I abort this batch, stop writing parameters and release the equipment occupancy.",
  "我作为操作员确认请求保持：写 Host_Hold，等待 PLC_Held，禁止盲写下一步。":
    "As the operator I request a hold: write Host_Hold, wait for PLC_Held, and do not blindly write the next step.",
  "我作为操作员确认解除保持，从当前工步继续四步握手。":
    "As the operator I release the hold and continue the four-step handshake from the current step.",
  "我作为主管确认跳过当前工步：仅在未写参的就绪/等待/确认相位，禁止跨阶段盲写。":
    "As the supervisor I skip the current step: only in a ready/wait/confirm phase with nothing written yet; skipping across phases is forbidden.",
  "我作为操作员确认本工步人工确认点已核对，允许继续且本工步不写 PLC。":
    "As the operator I confirm this step's manual checks are done; the step continues and writes nothing to the PLC.",
  "我作为质量审核人对照归档质检与四步握手，批准本批放行。":
    "As the quality reviewer I approve release of this batch after checking the archived results and the four-step handshake.",
  "我作为质量审核人对照归档质检与四步握手，拒收本批。":
    "As the quality reviewer I reject this batch after checking the archived results and the four-step handshake.",
  "我作为质量审核人对照规格判定本样品。":
    "As the quality reviewer I judge this sample against the specification.",
  "我导出的是整库备份，包含全部账号口令哈希与全部批记录，须按含敏感数据的介质保管。":
    "I am exporting a full database backup containing every account password hash and every batch record; handle it as sensitive media.",
  "请再次输入登录密码作为电子签名。": "Re-enter your sign-in password as the electronic signature.",
  // 顶栏实时徽标（脚本里的字面量，视图扫不到，所以显式登记）
  "实时": "Live",
  "刷新停滞": "Updates stalled",
  "重连中": "Reconnecting",
  "已断开": "Disconnected",
  "连接中": "Connecting",
  // 分页计数：带占位的键正是"中文也必须登记"的那一类（missing 处理器拿不到参数）。
  "本页 {0} 条 · 共 {1} 条": "{0} shown · {1} total",
  "{0} 条": "{0} rows",

  // ==== glossary.ts 悬停解释正文（经 tipOf() 在运行时取用，源码里没有 $t 字样） ====
  // ---- 术语名 ----
  "控制配方快照": "Control recipe snapshot",
  "完整性哈希": "Integrity hash",
  "A 写参": "A Write",
  "B 应答": "B Acknowledge",
  "C 看门狗": "C Watchdog",
  "D 归档步进": "D Archive & advance",
  "禁止盲写": "No blind writes",
  "电子签名": "Electronic signature",
  "设备占用": "Equipment occupancy",
  "设备驱动": "Equipment drivers",
  "设备类相库": "Class phase library",
  "握手履历": "Handshake history",
  "空闲安定": "Idle settle",
  "服务健康": "Service health",
  "监控工步": "Monitored step",
  "语义": "Semantic",
  "审批链": "Approval chain",
  "默认链": "Default chain",
  "随批缩放": "Scale with batch",
  "写PLC": "Write to PLC",
  "待保持": "Hold requested",
  "启动执行": "Start execution",
  "故障后重新排队": "Re-queue after fault",
  "质量放行": "Quality release",
  "质量拒收": "Quality rejection",
  "当前设备类": "Current class",
  "聚焦本页搜索": "Focus this page's search",
  "快捷键不可用": "Shortcut unavailable",
  // ---- 解释正文 ----
  "批次启动时把当时的主配方版本冻结成一份只读副本，批次执行期间即使主配方升版也仍按快照执行。":
    "The master recipe version is frozen as a read-only copy when the batch starts; even if the master recipe is later revised, the batch still runs against that snapshot.",
  "冻结后写入 frozenAt 并计算完整性哈希，用于事后证明「这批当时就是按这套参数跑的」。":
    "The freeze records frozenAt and computes an integrity hash, so it can be proven afterwards that this batch ran against exactly this set of parameters.",
  "对冻结的控制配方内容（工步、参数设定值、单元设备绑定、物料批号、缩放因子）计算的校验值，任何事后改动都会导致哈希不符。":
    "A checksum over the frozen control recipe (steps, setpoints, unit-equipment bindings, material lots, scale factor); any later change makes the hash mismatch.",
  "不匹配或损坏的快照禁止启动执行与质量放行。":
    "A snapshot that mismatches or is corrupt may not be started or released by quality.",
  "主控与 PLC 之间固定的写参握手。状态机七相位是真相；进度条 A/B/C/D 只是投影。":
    "The fixed parameter-write handshake between the host and the PLC. The seven-state machine states are the truth; the A/B/C/D progress bar is only a projection.",
  "A 写参 = 等待 PLC 就绪 + 下发参数；B 应答 = 等待 PLC 应答；C 看门狗 = 工步执行中；D 归档步进 = 收尾确认 / 可推进。完成、放行看批次状态，不写进握手列。":
    "A Write = waiting for PLC ready + writing parameters; B Acknowledge = waiting for the PLC reply; C Watchdog = step running; D Archive & advance = completing / ready to advance. Completion and release come from the batch status, not this handshake column.",
  "主控已选定目标工步，正在等 PLC 把 PLC_Ready 置位，表示设备可以接收参数。":
    "The host has picked the target step and is waiting for the PLC to raise PLC_Ready, meaning the equipment can accept parameters.",
  "未就绪期间不会写入任何参数（禁止盲写）。":
    "Nothing is written while the PLC is not ready (blind writes are forbidden).",
  "PLC 侧「允许接收参数」的位信号，四步握手的第一步依据。":
    "The PLC's \u2018allowed to receive parameters\u2019 bit, which the first step of the four-step handshake waits on.",
  "正在把本工步的参数写入 PLC 的参数区。":
    "Writing this step's parameters into the PLC's parameter area.",
  "写入后会回读比对，用于检出地址错误或数据被覆盖。":
    "The values are read back and compared afterwards, to catch a wrong address or overwritten data.",
  "工步设定值写入 PLC 的 16 个参数槽地址，与配方参数的槽序号一一对应。":
    "The sixteen parameter-slot addresses in the PLC that step setpoints are written to, one per recipe slot index.",
  "已置位 Trigger_Write 通知 PLC 取参，正在等 PLC 给出 Trigger_Ack 应答。":
    "Trigger_Write has been set to tell the PLC to pick up the parameters; waiting for its Trigger_Ack.",
  "超时未应答会判为握手故障并产生报警。":
    "No acknowledgement before the timeout is treated as a handshake fault and raises an alarm.",
  "PLC 已确认参数并开始执行本工步（如升温、保温）。":
    "The PLC has acknowledged the parameters and started this step (for example heating or holding).",
  "此阶段主控只监视 Step_Running / Step_Complete / Step_Error 与心跳。":
    "In this phase the host only monitors Step_Running / Step_Complete / Step_Error and the heartbeat.",
  "PLC 置位表示已开始执行本工步（如升温、保温），主控据此进入监视阶段。":
    "Set by the PLC to show the step has started (e.g. heating, holding); the host then enters its monitoring phase.",
  "PLC 报告 Step_Complete，主控正在确认本工步正常结束。":
    "The PLC reports Step_Complete and the host is confirming the step ended normally.",
  "PLC 置位表示本工步正常完成，主控据此收尾并推进到下一工步。":
    "Set by the PLC when the step finished normally; the host then wraps up and advances to the next step.",
  "PLC 置位表示本工步执行出错，主控据此报握手故障并停批。":
    "Set by the PLC when the step failed; the host reports a handshake fault and stops the batch.",
  "PLC 报 Step_Error 时带回的错误码，按设备手册定位具体故障。":
    "The error code the PLC returns with Step_Error; use the equipment manual to identify the fault.",
  "PLC 周期刷新的心跳计数，掉心跳判为通讯故障并触发报警。":
    "A heartbeat counter refreshed periodically by the PLC; losing it is treated as a comms fault and raises an alarm.",
  "已置位 Trigger_Write，正在等 PLC 报 Step_Running。":
    "Trigger_Write is set and the host is waiting for the PLC to report Step_Running.",
  "主控置位请求保持（暂停），等待 PLC 回 PLC_Held 后停剩余时长，禁止盲写下一步。":
    "Set by the host to request a hold (pause); it waits for PLC_Held before stopping the remaining time, and never blindly writes the next step.",
  "主控置位以通知 PLC 取新参数的触发信号，第三步依赖它的应答。":
    "The host's trigger signal telling the PLC to fetch new parameters; the third step depends on its acknowledgement.",
  "PLC 应答已保持，与 Host_Hold 配对完成保持握手。":
    "The PLC acknowledges the hold; paired with Host_Hold this completes the hold handshake.",
  "四步握手全部完成，主控可以推进到流程中的下一个工步。":
    "All four handshake steps are done, so the host may advance to the next step in the procedure.",
  "四步握手的前两拍：等 PLC_Ready，再写入参数并回读比对。":
    "The first two beats of the handshake: wait for PLC_Ready, then write parameters and read them back.",
  "读归档实测、复位握手位，确认后推进到下一工步。":
    "Reads the archived actuals, resets the handshake bits, and advances to the next step once confirmed.",
  "握手在某一步超时、回读不一致或 PLC 报 Step_Error 而中断，批次进入故障态。":
    "The handshake was interrupted by a timeout, a read-back mismatch or a PLC Step_Error; the batch is now faulted.",
  "未就绪期间不会写入任何参数（禁止盲写）。对应阶段：等待 PLC 就绪 / 下发参数。":
    "Nothing is written while the PLC is not ready (blind writes forbidden). Corresponding phases: waiting for PLC ready / writing parameters.",
  "此阶段不再写参。对应阶段：工步执行中。":
    "No parameters are written in this phase. Corresponding phase: step running.",
  "对应阶段：收尾确认 / 可推进。": "Corresponding phases: completing / ready to advance.",
  "超时未应答会判为握手故障。对应阶段：等待 PLC 应答。":
    "No answer before the timeout counts as a handshake fault. Corresponding phase: waiting for PLC ack.",
  "严禁绕过 PLC_Ready / 四步握手直接向 PLC 写参数，避免设备带着半成品参数运行。":
    "Writing to the PLC while bypassing PLC_Ready / the four-step handshake is strictly forbidden, so equipment never runs on half-written parameters.",
  "工步结束后等待 PLC 回到空闲（PLC_Ready）再允许下一步写参的秒数。":
    "Seconds to wait for the PLC to return to idle (PLC_Ready) after a step, before the next step may write parameters.",
  "过短会在设备尚未就绪时写下一步；过长则拖慢批次节拍。":
    "Too short writes the next step before the equipment is ready; too long slows the batch cadence.",
  "一台设备同一时刻只允许一个处于执行/排队/保持状态的批次，防止两个批次争抢同一套点表。":
    "Only one running, queued or held batch per equipment at a time, so two batches never fight over the same tag map.",
  "占用中的设备仍可生成控制配方快照，但不允许启动执行。":
    "A snapshot may still be frozen for occupied equipment, but execution may not be started on it.",
  "本批四步握手时序，以及快照相对当前生效主配方的漂移。":
    "This batch's handshake sequence, plus how the snapshot drifts against the currently released master recipe.",
  "当前主配方的设定值与本批次冻结快照不一致时，在对比中标记出来的差异。":
    "Setpoint differences flagged in the comparison when the current master recipe no longer matches this batch's frozen snapshot.",
  "批次执行全过程的归档记录：快照、工步执行、四步握手时序、实测质检、配方与批次电子签名含义。":
    "The archived record of the whole batch: snapshot, step execution, handshake sequence, measured quality results, and the recipe and batch e-signature meanings.",
  "批次已执行完成，等待质量对照电子批记录做放行或拒收。":
    "The batch has finished executing and is waiting for quality to release or reject it against the batch record.",
  "批次终检样品尚未由质量判定合格或不合格。":
    "The batch's final sample has not yet been judged by quality.",
  "对照归档质检与四步握手后签署放行，批次成为可交付状态。":
    "Release signed against the archived quality results and the handshake; the batch becomes deliverable.",
  "判定本批不合格，不能放行。需要填写拒收意见并电子签名。":
    "Judge this batch as failing; it cannot be released. A rejection comment and an e-signature are required.",
  "超差时必须写明偏差放行理由。需要电子签名。仅质量角色在待放行状态可用。":
    "An excursion must state the deviation-release justification. Requires an e-signature. Quality role only, and only while awaiting release.",
  "质检超差导致的保持需质量/主管审核后再恢复。需要电子签名。仅操作员或工艺主管。":
    "A hold caused by a quality excursion needs quality/supervisor review before resuming. Requires an e-signature. Operator or process supervisor only.",
  "本批执行期间产生的过程报警。页签角标是未确认条数。":
    "Process alarms raised while this batch ran. The tab badge counts unacknowledged ones.",
  "确认写入审计且保留履历。保持、跳步等操作仍在页顶，不在本页签。":
    "Acknowledgement is written to the audit trail and keeps its history. Hold, skip and similar actions stay in the page header, not this tab.",
  "异常时展开告警。库类型与备份入口在用户与备份页。":
    "Alerts expand on a problem. The database type and backup entry live under Users & backup.",
  "后端 API 与数据库可访问。正常只显示「正常」，库类型悬停可见。":
    "The backend API and database are reachable. Normally only \u2018OK\u2019 is shown; hover to see the database type.",
  "请求暂停本批：指令先落库，再写 Host_Hold，等 PLC_Held 后停剩余时长。":
    "Request a pause for this batch: the instruction is persisted first, then Host_Hold is written, and the remaining time stops once PLC_Held arrives.",
  "运行中点保持后，指令先落库再等 PLC_Held。监控立刻显示「保持已请求」，不会因为调度还没改状态而看起来没点上。":
    "When a hold is requested during execution, the instruction is persisted before waiting for PLC_Held. The monitor shows \u2018Hold requested\u2019 immediately, so it never looks like the click did nothing while the scheduler catches up.",
  "运行中点下去会立刻显示「保持已请求」，刷新或重启不会丢掉这条已签名指令。":
    "Clicking it while running shows \u2018Hold requested\u2019 at once; a refresh or restart will not lose this signed instruction.",
  "解除保持，调度从当前工步继续四步握手。":
    "Release the hold; the scheduler continues the four-step handshake from the current step.",
  "保持、恢复、跳过、中止仍在页顶。电子签名归档在电子批记录。":
    "Hold, resume, skip and abort remain in the page header. E-signatures are archived in the batch record.",
  "主管跳过当前工步并推进到下一工步，本步不写 PLC。":
    "A supervisor skips the current step and advances to the next; this step writes nothing to the PLC.",
  "需要电子签名。仅工艺主管。保持、故障，或运行中处于可跳相位（等就绪 / 保持 / 人工确认 / 主控等待）时可用。不可跳时按钮仍显示，悬停说明原因。":
    "Requires an e-signature. Process supervisor only. Available while held, faulted, or running in a skippable phase (await ready / hold / manual confirm / host wait). When a step cannot be skipped the button still shows, and hovering explains why.",
  "确认本工步已由操作员完成。调度将结束该工步且不向 PLC 写参。":
    "Confirms the operator has completed this step. The scheduler ends the step without writing parameters to the PLC.",
  "确认通过后才允许推进到下一工步。":
    "Advancing to the next step is allowed only after this confirmation.",
  "需要电子签名。只在人工确认工步等待中可用。":
    "Requires an e-signature. Only available while a manual-confirm step is waiting.",
  "冻结控制配方快照并排队，调度开始四步握手。":
    "Freeze the control recipe snapshot and queue the batch; the scheduler begins the four-step handshake.",
  "设备必须空闲。启动后即使主配方升版，本批仍按快照执行。仅操作员或工艺主管。":
    "The equipment must be idle. Once started, the batch keeps running against the snapshot even if the master recipe is revised. Operator or process supervisor only.",
  "设备必须空闲。快照完整性不符时禁止启动。":
    "The equipment must be idle, and a snapshot that fails its integrity check may not be started.",
  "在故障排除后沿用原控制配方快照重新排队，不会从主配方重新取参。":
    "Re-queue with the original control recipe snapshot after the fault is cleared; parameters are not re-read from the master recipe.",
  "需排除设备故障后处理批次，不得跳过握手直接写参。":
    "The equipment fault must be cleared before the batch is handled; the handshake may not be skipped to write parameters directly.",
  "停止本批次并释放设备占用。已执行工步保留在电子批记录中。":
    "Abort this batch and release the equipment occupancy. Steps already executed stay in the batch record.",
  "再次输入登录密码确系本人，弹窗展示固定签署含义（配方审核级或批次启停/跳步/放行），满足 21 CFR 11。":
    "Re-enter your password to prove it is you. The dialog shows a fixed signing meaning (recipe review level, or batch start/hold/skip/release) as required by 21 CFR 11.",
  "配方保存仍用工艺工程师声明（已落库文案不改）。批次启动、保持、跳步、放行各自有操作员或质量含义，写入审计详情与电子批记录。系统管理员不能代签这些动作。":
    "Recipe saves still use the process engineer's declaration (wording already stored is never rewritten). Batch start, hold, skip and release each carry an operator or quality meaning, written into the audit detail and the batch record. An administrator may not sign these on someone's behalf.",
  "配置用户、设备点表与相库，不能代签批次启停、跳步或配方审核。":
    "Configures users, equipment tag maps and the phase library, but may not sign batch start/stop, skips or recipe reviews for others.",
  "车间动作请用操作员 / 工艺主管账号；质量放行只用质量账号。":
    "Use an operator or process-supervisor account for shop-floor actions; quality release requires a quality account.",
  "以当前登录角色签署本审核节点（工艺主管或质量）。":
    "Sign this review node with your current role (process supervisor or quality).",
  "需要再次输入登录密码。质量节点通过后该版本生效，可被新批次冻结。":
    "Re-enter your sign-in password. Once the quality node passes, this version becomes effective and can be frozen by new batches.",
  "驳回本版工艺，草稿回到可编辑。需要填写原因并电子签名。":
    "Reject this procedure version and the draft becomes editable again. A reason and an e-signature are required.",
  "有未保存改动时离开本页会提示。生效版本不受影响。":
    "Leaving this page with unsaved changes prompts you. The released version is unaffected.",
  "把当前草稿的工步、连线与参数槽写回服务器，不会提交审核。":
    "Writes the current draft's steps, links and parameter slots back to the server; it does not submit for review.",
  "把草稿送入工艺主管 → 质量的签署路径，提交后本版不可再改工步。":
    "Puts the draft on the process-supervisor → quality signing path; once submitted the steps of this version can no longer be changed.",
  "需要电子签名。被驳回后可重新打开草稿再改。":
    "Requires an e-signature. After a rejection the draft can be reopened and edited.",
  "这份配方提交后要按顺序经过哪些人签核。":
    "Who must sign this recipe, in order, after it is submitted.",
  "链由管理员在「审批链配置」里维护，一条链里同一角色只签一次。":
    "Chains are maintained by an administrator under Approval chains; a role signs at most once per chain.",
  "留空即走默认链。改链只影响之后的提交：在审版本的节点在提交那刻已连同名称与签名含义一起冻结。":
    "Leave empty to use the default chain. Editing a chain only affects later submissions: an in-review version's nodes were already frozen, with their names and signing wording, at submission.",
  "全库必须且只能有一条默认链：取消某条的默认位，得同时把默认让给另一条。":
    "Exactly one default chain must exist; clearing one chain's default means handing the default over to another.",
  "配方没有专门指定审批链时走的那一条。":
    "The chain used when a recipe has not picked one itself.",
  "这个参数是什么量：工艺时长、速率，或不声明。":
    "What kind of quantity this parameter is: process duration, a rate, or undeclared.",
  "声明「工艺时长」后，工步剩余时间按它算，不再靠参数名里的“时长/时间/等待”猜。":
    "Declaring \u201cDuration\u201d makes the step's remaining time come from this parameter, instead of guessing from words like duration/time/wait in the name.",
  "声明「速率」则明确排除在时长之外（斜率、流量这类单位带 / 的量）。":
    "Declaring \u201cRate\u201d explicitly rules it out as a duration (slopes and flows, whose units contain a /).",
  "不声明时沿用按名称与单位推断的老规则，历史配方不必回填。":
    "When undeclared, the old name-and-unit inference applies, so historic recipes need no backfill.",
  "归档质检值时从设备点表读哪个实测点，填点表 Measured 里的键名。":
    "Which measured point to read from the equipment tag map when archiving a quality value; use a key from the tag map's Measured set.",
  "留空则按语义与名称推断（时长→HoldTime、温度→Temperature、压力→Pressure）。":
    "Leave blank to infer from the semantic and name (duration → HoldTime, temperature → Temperature, pressure → Pressure).",
  "非热处理的工艺（粘度、流量、计数）必须显式填，否则归档会静默漏掉这个参数；":
    "Non-thermal processes (viscosity, flow, counts) must state it explicitly, otherwise archiving silently drops this parameter;",
  "填了点表里没有的键，批次启动时会被拦下。":
    "A key that is not in the tag map blocks batch start.",
  "创建批次时按缩放因子乘这个设定值。":
    "Multiply this setpoint by the scale factor when the batch is created.",
  "温度、时长不要开。投料量、转移量这类随批量变的量才开。":
    "Do not enable it for temperature or time; only for quantities that scale with batch size, such as charge and transfer amounts.",
  "该参数槽是否写入 PLC 的 Param[n]。":
    "Whether this parameter slot is written to the PLC's Param[n].",
  "关掉则只留在配方里，不进写参计划。等待 / 质检 / 人工确认的槽默认不写。":
    "When off the value stays in the recipe but is left out of the write plan. Wait / quality / manual-confirm slots default to not writing.",
  "工步完成后是否把该槽归档为质检测点，并对照规格判超差。":
    "Whether this slot is archived as a quality checkpoint after the step and judged against its specification.",
  "关掉的槽仍可写 PLC，但不进电子批记录质检表。":
    "A slot with this off can still be written to the PLC, but it does not appear in the batch record's quality table.",
  "ISA-88 层级中的单元规程，是一段独立可并行执行的工艺单元（如淬火、回火）。":
    "A unit procedure in the ISA-88 hierarchy: a self-contained process unit that can run in parallel (for example quench or temper).",
  "ISA-88 层级中的操作，隶属于某个 Unit Procedure。":
    "An operation in the ISA-88 hierarchy, belonging to one unit procedure.",
  "批次控制国际标准的过程模型层级：Procedure → Unit Procedure → Operation → Phase。":
    "The process model hierarchy of the batch control standard: Procedure → Unit Procedure → Operation → Phase.",
  "本单元规程声明的设备类，决定从哪套相模板添加写 PLC 工步。":
    "The equipment class declared by this unit procedure, which decides which phase templates PLC-writing steps come from.",
  "该单元规程实际握手的 PLC。与主设备不同时，同一波次可并行四步握手。":
    "The PLC this unit procedure actually handshakes with. When it differs from the primary equipment, units in the same wave can run the four-step handshake in parallel.",
  "不同 Unit Procedure 可绑定到不同设备并行进行四步握手；同一设备仍串行。":
    "Different unit procedures may bind to different equipment and handshake in parallel; a single device still runs serially.",
  "主控下发给 PLC 的当前工步标识，PLC 据此选择执行哪一段工艺程序。":
    "The current step identifier the host sends to the PLC, which uses it to choose which programme to run.",
  "写入 PLC 点表 Step_Type 的整数，用来选现场程序，不是界面上的中文类型名。":
    "The integer written to the tag map's Step_Type to select a plant programme; it is not the Chinese type name shown in the UI.",
  "下发给 PLC 的程序号。默认等于工步类型整型；相模板可改成 9–99 以对应现场更多程序。":
    "The programme number sent to the PLC. It defaults to the step-type integer; phase templates may change it to 9–99 for additional plant programmes.",
  "等待 / 质检 / 人工确认不下发。0、7、8 保留给上位机工步。":
    "Wait / quality / manual-confirm steps send nothing. 0, 7 and 8 are reserved for host-side steps.",
  "空则用类型默认号（升温=1 … 转移=6）。自定义相如水冲洗、气缸保压用 9–99，必须和 PLC 程序表一致。":
    "Leave blank to use the type default (heat=1 … transfer=6). Custom phases such as water rinse or cylinder pressure hold use 9–99 and must match the PLC programme list.",
  "Heat / Transfer 只是程序号 1–6 的缺省别名。真正下发的是程序号；调度只看执行类。":
    "Heat / Transfer are just default aliases for programme numbers 1–6. What is actually sent is the programme number, and the scheduler only looks at the execution kind.",
  "调度只认四类行为：写 PLC、等待、质检、人工确认。":
    "The scheduler only knows four behaviours: write to PLC, wait, quality check, manual confirm.",
  "升温/搅拌等是写 PLC 的默认程序号别名。自定义相用程序号 9–99，不新增执行类。":
    "Heat-up, mix and so on are default programme-number aliases for PLC-writing phases. Custom phases use 9–99 rather than a new execution kind.",
  "相模板落库时挂上的默认程序号别名（升温=1 … 转移=6）。":
    "The default programme-number alias stored with a phase template (heat=1 … transfer=6).",
  "这一相在工艺上的名称。自定义相显示水冲洗、气缸保压，不是枚举别名转移、加压。":
    "The process name of this phase. Custom phases show as water rinse or cylinder pressure hold, not as the enum aliases transfer or pressurise.",
  "自定义相仍要选一个别名，同时把程序号改成 9–99。界面类型显示用相名称，不显示这个别名。":
    "A custom phase still needs an alias while its programme number is set to 9–99. The UI type column shows the phase name, not this alias.",
  "按设备类集中维护写 PLC 的相模板：名称、程序号和默认参数。":
    "PLC-writing phase templates maintained per equipment class: name, programme number and default parameters.",
  "从当前设备类相库插入一条相：名称、默认参数和 PLC 程序号一次带上。":
    "Insert a phase from the current class library, bringing its name, default parameters and PLC programme number along.",
  "写 PLC 工步只能从模板加。当前类没有模板时，用「管理相库」去设备页相库页签新增。":
    "PLC-writing steps can only be added from templates. If the class has none, use Manage phase library to add one in the equipment page's phase tab.",
  "相模板不在这里，在同一页的相库页签。":
    "Phase templates are not here, but in the phase library tab of the same page.",
  "设计器从这里添加工步。等待 / 质检 / 人工确认不进本库。驱动和点表在设备页签。":
    "The designer adds steps from here. Wait / quality / manual-confirm do not belong in this library. Drivers and tag maps are in the equipment tab.",
  "该类设备允许执行的写 PLC 相：名称、默认参数和程序号。":
    "The PLC-writing phases this class of equipment may execute: name, default parameters and programme number.",
  "入口在设备页的相库页签。程序号 9–99 对应现场自定义程序。等待/质检/确认不上本库。编码全局唯一。":
    "The entry is the phase library tab on the equipment page. Programme numbers 9–99 are plant-defined. Wait/quality/confirm are not kept here. Codes are globally unique.",
  "只列出允许本单元程序号、且与单元声明类相符的设备。质检汇合从唯一汇入单元继承设备类。":
    "Only equipment that allows this unit's programme number and matches the unit's declared class is listed. The quality join inherits the class from the single incoming unit.",
  "选项按设备类相库与单元声明类过滤。具体类不一致不可选；未分类和通用环回站不拦。":
    "Choices are filtered by class phase library and the unit's declared class. A mismatched specific class is unselectable; unclassified and general loop-back stations are not blocked.",
  "本站 PLC 的协议、地址、握手点表与仿真故障。":
    "This station's PLC protocol, address, handshake tag map and simulator faults.",
  "本批控制配方绑定的默认 PLC。多单元时可再为其他单元规程另选设备。":
    "The default PLC bound to this batch's control recipe. With several units, other unit procedures may pick their own equipment.",
  "本批实时测点曲线，温度与压力分轴显示。":
    "Live tag curves for this batch, with temperature and pressure on separate axes.",
  "页签未打开时仍接收采样，打开后再按当前宽度绘图。":
    "Samples keep being collected while the tab is closed, and are drawn at the current width once it opens.",
  "本页工步页签：工艺画布、冻结设定矩阵、写参计划和 ISA-88 时间线。":
    "This page's step tab: procedure canvas, frozen setpoint matrix, PLC write plan and ISA-88 timeline.",
  "判定入口在该批次的电子批记录，不在来料批列表。":
    "Judging happens in that batch's electronic batch record, not in the incoming lot list.",
  // ---- 快捷键与页面行为 ----
  "当前页可用的键盘动作。问号打开说明，斜杠聚焦搜索；列表里用 Tab 停住一行，按 Enter 打开。":
    "Keyboard actions available on this page. Question mark opens this help, slash focuses search; in lists Tab stops on a row and Enter opens it.",
  "打开或关闭本说明": "Open or close this panel",
  "把光标放到本页筛选框并选中已有文字，便于立刻改关键词。":
    "Puts the cursor in this page's filter box and selects the existing text so a keyword can be edited immediately.",
  "本页没有搜索框时该快捷键无效。已在输入框里时斜杠会当成普通字符。":
    "This shortcut does nothing when the page has no search box. While you are already typing in a field, slash is treated as a normal character.",
  "列出当前页真正能按的键，以及灰色的「当前不可用」项。":
    "Lists the keys that actually work on this page, plus the greyed-out \u2018currently unavailable\u2019 ones.",
  "没有快捷键，必须点按钮并填写意见。":
    "There is no shortcut; you must press the button and enter a comment.",
  "没有快捷键，避免在审核时误触。":
    "No shortcut, to avoid mis-firing it during a review.",
  "说明打开时只响应 Esc 或再次按问号关闭，避免误触发保持或提交。":
    "While this panel is open only Esc, or pressing the question mark again, closes it — so a stray key cannot trigger a hold or a submit.",
  "输入框里的字母键不会触发动作，避免打字误保持或误提交。中止、质量拒收、配方驳回没有快捷键，必须点按钮并电子签名。":
    "Letter keys do not fire actions while you are typing, so typing cannot accidentally request a hold or submit. Abort, quality rejection and recipe rejection have no shortcut at all — they require the button and an e-signature.",
  "当前页没有对应动作，或当前状态、角色不允许。":
    "This page has no such action, or the current state or role does not allow it.",
  // glossary 尾巴：视图里以整句呈现的解释
  "签署含义在电子批记录，不在本页。": "The signing meaning is in the electronic batch record, not on this page.",
  "工步执行中只监视心跳、超时与 Step_Complete / Step_Error。": "While the step runs, only the heartbeat, timeouts and Step_Complete / Step_Error are monitored.",
  "需要电子签名。仅操作员或工艺主管。没有快捷键，避免在车间误触。": "Requires an e-signature. Operator or process supervisor only. There is no shortcut, to avoid mis-firing it on the shop floor.",
  "选中会写进配方并随保存持久化。重开不再按程序号把 PROCESS 推断成 FURNACE。开批优先选该类设备。": "When selected it is stored with the recipe and persisted on save. Reopening no longer infers PROCESS as FURNACE from the programme number, and batch start prefers equipment of this class.",
  "未改时保存默认 DB10.20 起每 4 字节一槽。现场点表不同必须改这里，否则写参落到错误偏移，回读不一致或更糟——写到看似合理的错误地址。": "Unsaved changes keep the default of one slot every 4 bytes from DB10.20. If the plant tag map differs you must change it here, otherwise writes land at the wrong offset — a read-back mismatch, or worse, a plausible-looking wrong address.",
  // ---- 快捷键说明面板：分组名与行名是数据（label/group），只能 $t(row.label)，扫不到字面量 ----
  "全局": "Global",
  "批次监控": "Batch monitor",
  "配方审核": "Recipe review",
  "空格": "Space",
  // 键位名与提示行：formatChord 的结果是运行时参数，中文侧靠占位身份表拿回整句。
  "快捷键 {0}。": "Shortcut {0}.",
  "快捷键 {0}，在输入框里也可按。": "Shortcut {0} — also works inside input fields.",

  // ---- 界面壳子（列表页计数 / 空态 / 校验与确认框 / 电子批记录字段名）----
  "设备 / 握手点表": "Equipment / handshake tag map",
  "编辑相模板": "Edit phase template",
  "展开菜单": "Expand menu",
  "收起菜单": "Collapse menu",
  "确认退出当前账号？": "Sign out of the current account?",
  "退出确认": "Confirm sign out",
  "报警列表加载失败：{0}": "Failed to load alarms: {0}",
  "{0} / {1} 条": "{0} of {1} items",
  "没有匹配的报警": "No matching alarms",
  "暂无未确认报警": "No unacknowledged alarms",
  "暂无过程报警": "No process alarms",
  "已确认报警 {0}": "Alarm {0} acknowledged",
  "确认当前列表中的 {0} 条未确认报警？确认后仍保留履历，供批次放行与追溯。": "Acknowledge the {0} unacknowledged alarms in this list? History is kept for batch release and traceability.",
  "批量确认报警": "Acknowledge alarms",
  "全部确认": "Acknowledge all",
  "没有已批准的主配方": "No approved master recipe",
  "请确认批次是否已执行完成，或返回批次列表重试。": "Check whether the batch has finished, or go back to the batch list and retry.",
  "本页归档控制配方快照、ISA-88 工步、四步握手时序、实测质检与配方/批次电子签名含义。执行完成后由质量电子签名放行或拒收。": "This page archives the control-recipe snapshot, ISA-88 steps, four-step handshake timing, measured quality results and the recipe/batch e-signature meanings. Quality signs the release or rejection once execution completes.",
  "快照完整性": "Snapshot integrity",
  "冻结时间": "Frozen at",
  "放行人": "Released by",
  "放行时间": "Released at",
  "放行意见": "Release comment",
  "工步编码": "Step code",
  "结果": "Result",
  "样品号": "Sample no.",
  "判定": "Disposition",
  "取样人": "Sampled by",
  "快照设定": "Snapshot setpoint",
  "握手故障批次": "Handshake-faulted batches",
  "未确认报警": "Unacknowledged alarms",
  "保持中批次": "Batches on hold",
  "待质量放行": "Awaiting quality release",
  "执行中批次": "Running batches",
  "排队批次": "Queued batches",
  "已批准配方": "Approved recipes",
  "草稿配方": "Draft recipes",
  "搜索设备类 / 相模板": "Search equipment class / phase template",
  "搜索编码 / 名称 / 主机": "Search code / name / host",
  "设备列表加载失败：{0}": "Failed to load equipment: {0}",
  "没有匹配的设备": "No matching equipment",
  "暂无设备": "No equipment yet",
  "未分类": "Unclassified",
  "是": "Yes",
  "否": "No",
  "查看": "View",
  "{0} / {1} 个相模板": "{0} of {1} phase templates",
  "共 {0} 个相模板": "{0} phase templates",
  "{0} / {1} 台": "{0} of {1} units",
  "共 {0} 台": "{0} units",
  "保持未 Ready（禁止写参）": "Stay not Ready (block parameter writes)",
  "回读不一致（拒绝 Trigger_Write）": "Corrupt read-back (reject Trigger_Write)",
  "Trigger 后不应答": "No answer after Trigger",
  "PLC 报 Step_Error": "PLC reports Step_Error",
  "丢失心跳": "Drop heartbeat",
  "清除故障": "Clear fault",
  "清除 {0} 上的仿真故障？": "Clear the simulated fault on {0}?",
  "向 {0} 注入「{1}」？若该设备上有运行中批次，会进入故障或保持。": "Inject “{1}” into {0}? A running batch on this equipment will fault or hold.",
  "清除": "Clear",
  "注入": "Inject",
  "请输入用户名和密码": "Enter your username and password",
  "物料批次加载失败：{0}": "Failed to load material lots: {0}",
  "没有匹配的物料批": "No matching lots",
  "暂无物料批次": "No material lots yet",
  "物料批次号已存在，请换一个。": "That lot number already exists; use another.",
  "请填写物料批次号。": "Enter the lot number.",
  "请填写物料编码。": "Enter the material code.",
  "已登记来料批": "Received lot recorded",
  "审批链加载失败：{0}": "Failed to load approval chains: {0}",
  "编辑 {0}": "Edit {0}",
  "新建审批链": "New approval chain",
  "（没有节点）": "(no nodes)",
  "保存审批链": "Save approval chain",
  "确认把「{0}」保存为 {1} 级审批链。": "Save “{0}” as a {1}-step approval chain.",
  "审批链已保存": "Approval chain saved",
  "删除后，仍选用 {0} 的配方必须改链才能提交。在审版本不受影响。": "After deletion, recipes still using {0} must switch chain before they can be submitted. Versions already in review are unaffected.",
  "删除审批链 {0}": "Delete approval chain {0}",
  "删除审批链": "Delete approval chain",
  "确认删除「{0}」。": "Confirm deleting “{0}”.",
  "审批链已删除": "Approval chain deleted",
  "配方列表加载失败：{0}": "Failed to load recipes: {0}",
  "没有匹配的主配方": "No matching master recipes",
  "暂无主配方": "No master recipes yet",
  "当前筛选条件下没有配方": "No recipes under the current filter",
  "配方编码已存在，请换一个。": "That recipe code already exists; use another.",
  "请填写配方编码。": "Enter the recipe code.",
  "请填写配方名称。": "Enter the recipe name.",
  "用户列表加载失败：{0}": "Failed to load users: {0}",
  "没有匹配的用户": "No matching users",
  "暂无用户": "No users yet",
  "编辑用户": "Edit user",
  "登录名已存在。": "That sign-in name already exists.",
  "请填写登录名。": "Enter the sign-in name.",
  "登录名至少 3 位。": "The sign-in name must be at least 3 characters.",
  "请填写显示名。": "Enter the display name.",
  "新建用户必须设置密码。": "A new user needs a password.",
  "密码至少 8 位。": "The password must be at least 8 characters.",
  "下载 SQLite 整库备份": "Download full SQLite backup",
  "备份失败。": "Backup failed.",
  // 路由守卫与浏览器标签页：页名取自 router 的中文名单，整句要单独登记。
  "该页面": "this page",
  "当前账号（{0}）无权访问「{1}」，已回到运行总览。": "The current account ({0}) may not open “{1}”; you are back on the overview.",
  "BRMES 工艺配方管理": "BRMES process recipe management",
  // 快照完整性四态（integrity.ts 的字典值）与批记录里两处小节标题、一台设备多单元时的兜底句。
  "完整性有效": "Seal valid",
  "历史快照(无哈希)": "Legacy snapshot (no hash)",
  "完整性失败": "Seal mismatch",
  "快照损坏": "Snapshot corrupt",
  "快照版本过新": "Snapshot from a newer version",
  "ISA-88 控制配方（快照）": "ISA-88 control recipe (snapshot)",
  "PLC 写参计划（拓扑顺序，禁止盲写）": "PLC write plan (topological order, no blind writes)",
  "全部使用主设备": "All on the primary equipment",
  // 页头两行：中文语序里的「快照 X」在英文里同序，所以整句登记而不是拆开拼。
  "电子批记录 · {0}": "Electronic batch record · {0}",
  "快照：{0}": "Snapshot: {0}",
  // ---- 电子批记录页的动作提示与签名弹窗（默认中文态原样返回，e2e 仍按中文标题定位）----
  "超差": "Out of spec",
  "电子批记录加载失败：{0}": "Failed to load the batch record: {0}",
  "电子批记录加载失败": "Batch record failed to load",
  "导出 PDF 失败": "PDF export failed",
  "样品编号": "Sample code",
  "实验室取样": "Lab sampling",
  "已登记样品": "Sample registered",
  "不合格必须填写对照规格的意见。": "A failure requires a comment against the specification.",
  "请填写判定意见（可空）。": "Enter the disposition comment (optional).",
  "样品判定": "Sample disposition",
  "样品已判定": "Sample disposition recorded",
  "请对照归档质检与四步握手填写放行意见。超差时必须说明偏差放行理由。":
    "Write the release comment against the archived quality results and the four-step handshake. An out-of-spec release must state the deviation rationale.",
  "批次已质量放行": "Batch released by quality",
  "请填写拒收意见（对照质检超差或握手异常）。": "Enter the rejection reason (out-of-spec result or handshake anomaly).",
  "批次已质量拒收": "Batch rejected by quality",
  // ---- 电子批记录页（BatchRecord：字段与小节标题，落库原文不在这里）----
  "返回监控": "Back to monitor",
  "打印": "Print",
  "导出 PDF/A": "Export PDF/A",
  "批次抬头": "Batch header",
  "物料投料与产出谱系": "Material input and output genealogy",
  "本批次未绑定物料谱系（快照仍可仅有 LotNumber 字符串）。": "This batch has no linked material genealogy (the snapshot may still carry LotNumber strings only).",
  "配方电子签名": "Recipe e-signatures",
  "批次执行电子签名": "Batch execution e-signatures",
  "尚无启动 / 保持 / 跳步 / 放行签署（旧批次可在审计日志查看动作码）。": "No start, hold, skip or release signatures yet (older batches show their action codes in the audit log).",
  "本批次无握手/调度报警。": "No handshake or scheduling alarms for this batch.",
  "整张工艺 → 单元规程 → 操作 → 工步。下图画布与监控页同源，按冻结连线排布。": "The whole procedure → unit procedures → operations → phases. The canvas below shares its source with the monitor page and lays out the frozen connections.",
  "冻结设定矩阵（设定 / 归档实测）": "Frozen setpoint matrix (setpoints / archived actuals)",
  "实验室样品（LIMS，与 PLC 测点分开）": "Lab samples (LIMS, kept separate from PLC tags)",
  "取样": "Sample",
  "本批次无实验室样品。": "No lab samples for this batch.",
  "归档质检": "Archived quality checks",
  "尚无工步归档质检。": "No phase has an archived quality check yet.",
  "四步握手时序（禁止盲写）": "Four-step handshake timing (no blind writes)",
  // ---- 混合插值节点与视图脚本里自己拼的串（计数、校验、汇合提示、签署含义）----
  "{0} 不是可读的 JSON，请选择导出的 .json 配方包。": "{0} is not readable JSON; choose a .json recipe package exported by this system.",
  "{0} 里没有 recipes 数组，不像是本系统导出的配方包。": "{0} has no recipes array, so it does not look like a package exported by this system.",
  "导入完成：新建 {0}，跳过 {1}": "Import finished: {0} created, {1} skipped",
  "写参槽 {0} … {1}。": "Parameter slots {0} … {1}.",
  "PLC 握手位 · {0}": "PLC handshake tags · {0}",
  "删除 {0} {1} 只影响新编排，已保存工步仍带原程序号。": "Deleting {0} {1} only affects new arrangements; saved steps keep their original programme number.",
  "新增工步：{0}": "Added steps: {0}",
  "删除工步：{0}": "Removed steps: {0}",
  "规格 {0}~{1}": "Spec {0}~{1}",
  "实测 {0}": "Actual {0}",
  "程序 {0}": "Programme {0}",
  "再次输入登录密码": "Re-enter your sign-in password",
  "{0} · 电子签名": "{0} · e-signature",
  "签名并确认": "Sign and confirm",
  "请填写{0}。": "Enter the {0}.",
  "电子签名需要再次输入登录密码。": "An e-signature requires you to re-enter your sign-in password.",
  "握手故障 · {0}": "Handshake fault · {0}",
  "尚未开始四步握手": "The four-step handshake has not started",
  "剩余 {0}s": "{0}s left",
  "工步 {0} 必须填写单元规程": "Step {0} needs a unit procedure",
  "工步 {0} 必须填写操作": "Step {0} needs an operation",
  "工步 {0} 的 PLC 程序号 {1} 无效（写 PLC 用 1–6 或 9–99）": "PLC programme {1} of step {0} is invalid (use 1–6 or 9–99 when writing the PLC)",
  "缩放 ×{0}": "Scale ×{0}",
  "物料 {0}": "Material {0}",
  "确认本页 {0} 条": "Acknowledge {0} on this page",
  "{0} 正被 {1} 占用。可以先生成控制配方快照，启动执行须等设备空闲（禁止双批盲写）。": "{0} is occupied by {1}. You may freeze the control-recipe snapshot now, but starting execution must wait until the equipment is free (no blind writes for two batches).",
  "未启用": "Disabled",
  "类 {0} 不允许程序 {1}": "Class {0} does not allow programme {1}",
  "单元声明 {0}，设备属于 {1}": "The unit declares {0}, the equipment belongs to {1}",
  "占用 {0}": "Occupied by {0}",
  "无法打开创建窗口：{0}": "Cannot open the create dialog: {0}",
  "请选择主设备。": "Choose the primary equipment.",
  "主设备不兼容：{0}": "Primary equipment incompatible: {0}",
  "{0} 未绑定设备。": "{0} has no equipment bound.",
  "{0} 绑定的设备不兼容：{1}": "The equipment bound to {0} is incompatible: {1}",
  "确认全部 {0} 条": "Acknowledge all {0}",
  "确认本批次 {0} 条未确认报警？确认后仍保留履历。": "Acknowledge the {0} unacknowledged alarms of this batch? History is kept.",
  "已确认 {0} 条报警": "{0} alarms acknowledged",
  "已确认 {0} 条，其余失败：{1}": "{0} acknowledged, the rest failed: {1}",
  "谱系 · {0}": "Genealogy · {0}",
  "产品 {0} · 审核版 v{1} · 状态 {2}": "Product {0} · under review v{1} · status {2}",
  "变更说明：{0}": "Change note: {0}",
  "相对生效版 v{0} 的差异": "Differences against the effective version v{0}",
  "配方详情加载失败：{0}": "Failed to load the recipe: {0}",
  "当前节点：{0}": "Current node: {0}",
  "{0} 没有相模板。": "{0} has no phase templates.",
  "待审": "Awaiting review",
  "意见：{0}": "Comment: {0}",
  "当前工步 {0} {1}": "current step {0} {1}",

  "已将 {0} 汇合到当前工步": "Joined {0} into the current step",
  "未汇合 {0}：{1}": "Not joined {0}: {1}",
  "默认链：{0} · {1}": "Default chain: {0} · {1}",
  "默认链（管理员未配置）": "Default chain (not configured by an administrator)",
  "改审批链": "Change approval chain",
  "确认这份配方之后提交时走「{0}」。在审版本已冻结，不受影响。": "Confirm that this recipe will be submitted through “{0}”. Versions already in review are frozen and unaffected.",
  "审批链已更新": "Approval chain updated",
  "审核意见（通过）": "Review comment (approve)",
  "驳回原因": "Rejection reason",
  "已重新打开为草稿，可改工艺后再次提交审核。": "Reopened as a draft; revise the procedure and submit it for review again.",
  "已创建草稿 v{0}，在此版本改工艺后再提交审核。": "Draft v{0} created; revise the procedure in this version before submitting for review.",
  "GxP：保存会改写草稿 Procedure / Steps / Parameters。签署含义：我作为工艺工程师确认本次变更准确，并记录变更原因。": "GxP: saving rewrites the draft Procedure / Steps / Parameters. Meaning: as the process engineer I confirm this change is accurate and record its reason.",
  "签署含义：我作为工艺工程师确认本版本 Procedure / Steps 与 Parameters / Setpoints 准确，提交多级审核。": "Meaning: as the process engineer I confirm this version's Procedure / Steps and Parameters / Setpoints are accurate, and submit it for multi-level review.",
  "签署含义：我作为工艺工程师确认将被驳回版本重新打开为草稿，并继续修订 Procedure / Setpoints。": "Meaning: as the process engineer I confirm the rejected version is reopened as a draft so Procedure / Setpoints can keep being revised.",
  "签署含义：我作为工艺工程师确认基于当前生效版本另开草稿，本条变更说明会写进新版本履历。": "Meaning: as the process engineer I confirm a new draft is opened from the current effective version, and this change note is written into the new version history.",
  "写入并回读一致": "Write and read-back match",
  "等待工步启动": "Waiting for the step to start",
  "心跳与超时监控": "Heartbeat and timeout monitoring",
  "读实测、复位、步进": "Read actuals, reset, advance",
  "跨单元连线必须从该单元末工步进入目标单元首工步；同单元必须按工步顺序": "A cross-unit connection must enter the target unit's first step from that unit's last step; within one unit steps must follow their order",
  // ---- 实时徽标提示、写参策略与配方设计器的混排句（PLC_Ready 那句来自引擎，按值查表、查不到原样显示）----
  "禁止写 PLC": "Must not write to the PLC",
  "（{0} 秒前）": "({0}s ago)",
  "最后一次成功取数：{0}。": "Last successful read: {0}.",
  "还没有成功的取数请求。": "No successful read yet.",
  "{0}点一下立即重拉本页数据，不必等下一个 {1} 秒周期。": "{0}Click to reload this page immediately, without waiting for the next {1}s poll.",
  "推送连接正常，但已经 {0} 秒没有取到数据——接口在报错或后端不可达，屏幕上的数字可能不是最新。{1}": "The push connection is fine, but no data has arrived for {0}s — the API is failing or the backend is unreachable, so the numbers on screen may be stale.{1}",
  "实时推送已连接：握手阶段变化、报警、设备占用即时到达；数据仍每 {0} 秒轮询兜底。{1}{2}": "Live push connected: handshake phase changes, alarms and equipment occupancy arrive immediately; data is still polled every {0}s as a backstop.{1}{2}",
  "（30 秒内没有执行事件，多半是当下没有批次在跑。）": "(No execution events within 30s — most likely no batch is running right now.)",
  "实时推送正在重连。页面仍每 {0} 秒轮询刷新，但执行事件可能滞后——不要据此判断瞬时状态。{1}": "Live push is reconnecting. The page still polls every {0}s, but execution events may lag — do not read instantaneous state from this.{1}",
  "实时推送已断开，目前仅靠 {0} 秒轮询维持，数据可能滞后。请检查后端服务与网络。{1}": "Live push is down; only the {0}s poll remains, so data may be stale. Check the backend service and the network.{1}",
  "正在建立实时推送连接…{0}": "Establishing the live push connection…{0}",
  "产品 {0} · {1} v{2}": "Product {0} · {1} v{2}",
  "无草稿": "No draft",
  "只读浏览 v{0}（{1}）。草稿才可改工步与参数。": "Read-only view of v{0} ({1}). Only a draft allows editing steps and parameters.",
  "参数 {0} 槽": "{0} parameter slots",
  "审核": "Review",
  // 合并双栏签名框的栏名与占位（eBR 的放行 / 拒收 / 样品判定也走这个框）。
  "必填": "Required",
  "可选": "Optional",
  "登录密码（电子签名）": "Sign-in password (e-signature)",
  "判定意见": "Disposition comment",
  "拒收意见": "Rejection comment",
  "PLC_Ready 后写参并回读，再置 Trigger_Write": "After PLC_Ready, write the parameters, read them back, then set Trigger_Write",
  // 趋势与报警的取数口径：抽稀/开窗只发生在读的一侧，界面必须把这件事说出来。
  "共 {0} 条样本，全部绘出。": "{0} samples, all plotted.",
  "仅显示最近 {0} 条，本批共 {1} 条报警；「确认全部」只作用于上面列出的这些。":
    "Showing only the latest {0} of {1} alarms for this batch; \"Acknowledge all\" applies to the listed rows only.",
  "登记时间": "Registered at",
  // 每日自动备份（DatabaseBackup / DailyBackupHostedService）与备份卡片。
  "数据库每日备份": "Daily database backup",
  "立即备份一份": "Back up now",
  "最近一次备份失败：{0}": "Last backup failed: {0}",
  "还没有落下任何一份备份。定时任务每天 {0} (UTC) 运行，也可以点上面的立即备份。": "No backup has landed yet. The scheduled job runs daily at {0} (UTC), or use \"Back up now\" above.",
  "自动备份": "Automatic backup",
  "已启用": "Enabled",
  "已停用": "Disabled",
  "每天 (UTC)": "Daily (UTC)",
  "保留份数": "Copies kept",
  "下次执行": "Next run",
  "备份目录": "Backup folder",
  "暂无备份文件": "No backup files yet",
  "文件": "File",
  "大小": "Size",
  "备份只留在服务器本机，超出保留份数的旧快照会被删掉；每次成功与失败都写进操作审计。": "Backups stay on this server, and snapshots beyond the kept count are deleted. Every success and failure lands in the audit trail.",
  "管理员可维护账号。库每天自动落一份快照（见下方），「下载 SQLite」是把整库取走一份、需要电子签名；配方包在配方列表页导出导入。": "Administrators manage accounts here. The database writes one snapshot automatically every day (see below); \"Download SQLite\" takes a copy of the whole database and requires an e-signature. Recipe packages are exported and imported from the recipe list.",
  "已落一份备份：{0}": "Backup written: {0}",
  "系统": "System",
  "数据库备份": "Database backup",
  "数据库备份失败": "Database backup failed",
  "数据库维护": "Database maintenance",
  // 手工维护：没做 VACUUM 是安全判断，界面要按这个口径说话。
  "立即维护": "Maintain now",
  "已更新统计信息，VACUUM 回收 {0}。": "Statistics refreshed; VACUUM reclaimed {0}.",
  "已更新统计信息；本轮未做 VACUUM（{0}）。": "Statistics refreshed; no VACUUM this round ({0}).",
  // 运行总览的失败态：「不知道」必须和"确认是零"长得不一样，文案两侧都得说清这件事。
  "运行数据取数失败：{0}": "Overview data failed to load: {0}",
  "还没有取到任何数据，所有计数显示为「—」而不是 0；页面每 4 秒自动重试。": "No data has been retrieved yet, so every count shows \"—\" rather than 0; the page retries every 4 seconds.",
  "下面的数字是最后一次成功取数的结果，已经不代表当前状态；页面每 4 秒自动重试。": "The numbers below are from the last successful read and no longer reflect the current state; the page retries every 4 seconds.",
  "数据中断": "Data feed down",
  "数据不可用": "Data unavailable",
  "后端返回 {0}": "Backend returned {0}",
  "无法连接后端": "Cannot reach the backend",
  "推送连接正常，但还没有取到过一次数据——接口在报错或后端不可达，屏幕上的数字不可信。{0}":
    "The push connection is fine, but no read has ever succeeded — the API is erroring or the backend is unreachable, so the numbers on screen cannot be trusted. {0}",
  // 趋势取样与握手履历的读法说明：取样覆盖整批，履历只是被截了。
  "趋势覆盖整批，每 {0} 条样本取 1 点绘出（本批共 {1} 条）。样本不删除，电子批记录仍用全量数据。":
    "The trend spans the whole batch, plotting 1 point per {0} samples ({1} samples in this batch). Samples are never deleted; the electronic batch record still uses full-fidelity data.",
  "仅显示最近 {0} 条，本批共 {1} 条握手事件；完整履历见电子批记录。":
    "Showing only the latest {0} of {1} handshake events for this batch; the full trail is in the electronic batch record.",
};

export default en;
