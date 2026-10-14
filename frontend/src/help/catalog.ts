import type { UserRole } from "../api/types";

export type HelpLocale = "zh-CN" | "en";
export type HelpCategoryId =
  | "start"
  | "recipes"
  | "execution"
  | "quality"
  | "materials"
  | "system";

export interface HelpStep {
  title: string;
  text: string;
}

export interface HelpSection {
  id: string;
  title: string;
  intro?: string;
  steps?: readonly HelpStep[];
  bullets?: readonly string[];
  note?: string;
}

export interface HelpLink {
  label: string;
  to: string;
}

export interface HelpCopy {
  title: string;
  summary: string;
  keywords: readonly string[];
  sections: readonly HelpSection[];
  links: readonly HelpLink[];
}

export interface HelpArticle {
  id: string;
  category: HelpCategoryId;
  appliesTo: readonly UserRole[];
  contentVersion: string;
  owner: Readonly<Record<HelpLocale, string>>;
  reviewedAt: string;
  content: Readonly<Record<HelpLocale, HelpCopy>>;
}

export const HELP_CATEGORIES: readonly { id: "all" | HelpCategoryId; label: string }[] = [
  { id: "all", label: "全部主题" },
  { id: "start", label: "快速入门" },
  { id: "recipes", label: "配方与审核" },
  { id: "execution", label: "批次执行" },
  { id: "quality", label: "质量与放行" },
  { id: "materials", label: "设备与物料" },
  { id: "system", label: "系统管理" }
];

const allRoles: readonly UserRole[] = [
  "Admin", "ProcessEngineer", "Supervisor", "Quality", "Operator"
];

export const HELP_ARTICLES: readonly HelpArticle[] = [
  {
    id: "start-here",
    category: "start",
    appliesTo: allRoles,
    contentVersion: "1.0",
    owner: { "zh-CN": "待指定", en: "Unassigned" },
    reviewedAt: "2026-10-04",
    content: {
      "zh-CN": {
        title: "先从这里开始",
        summary: "认识导航、角色可见范围和页面帮助入口，快速找到安全的下一步。",
        keywords: ["导航", "菜单", "角色", "语言", "主题", "本页说明", "快速开始"],
        sections: [
          {
            id: "navigation",
            title: "导航与角色",
            steps: [
              { title: "从侧栏进入模块", text: "侧栏只显示当前账号可访问的功能。详情页仍归属对应的主模块，无法访问时按系统提示联系管理员核对角色。" },
              { title: "先读当前页提示", text: "页面标题旁的“？”说明当前页面用途、常用操作和注意事项；需要跨页面流程时，使用其中的“查看完整说明”跳转到本中心。" }
            ]
          },
          {
            id: "preferences",
            title: "界面偏好",
            bullets: [
              "顶栏可切换简体中文/英文以及深色/浅色主题；这些偏好保存在当前浏览器。",
              "帮助文章中的角色标签用于筛选适用内容，不会授予功能权限。实际操作仍受页面和服务端授权控制。"
            ]
          },
          {
            id: "safe-use",
            title: "遇到阻止或不确定时",
            note: "签署、批次处置、设备写入和数据库操作可能产生不可逆或受审计的影响。若前置数据不完整、状态不符或页面明确禁用操作，应先停止并核对原因，不要尝试绕过校验。"
          }
        ],
        links: [
          { label: "运行总览", to: "/dashboard" },
          { label: "主配方设计", to: "/recipes" }
        ]
      },
      en: {
        title: "Start here",
        summary: "Learn the navigation, role visibility, and page-help entry points before choosing the next safe action.",
        keywords: ["navigation", "menu", "roles", "language", "theme", "page guide", "getting started"],
        sections: [
          {
            id: "navigation",
            title: "Navigation and roles",
            steps: [
              { title: "Open a module from the sidebar", text: "The sidebar shows features available to your account. Detail pages stay within their parent module. If access is denied, follow the system warning and ask an administrator to verify your role." },
              { title: "Read the page guide first", text: "The question-mark button by a page title explains that page's purpose, common actions, and cautions. Use its full-guide link for cross-page procedures." }
            ]
          },
          {
            id: "preferences",
            title: "Interface preferences",
            bullets: [
              "The header switches between Simplified Chinese and English, and between dark and light themes. These preferences are stored in the current browser.",
              "Role labels in help are filters for relevant content; they do not grant feature permissions. Page and server authorization still control actions."
            ]
          },
          {
            id: "safe-use",
            title: "When blocked or unsure",
            note: "Signatures, batch dispositions, PLC writes, and database operations can have irreversible or audited effects. If prerequisites are incomplete, state is unexpected, or the page disables an action, stop and resolve the cause instead of bypassing validation."
          }
        ],
        links: [
          { label: "Dashboard", to: "/dashboard" },
          { label: "Master recipes", to: "/recipes" }
        ]
      }
    }
  },
  {
    id: "recipe-lifecycle",
    category: "recipes",
    appliesTo: ["Admin", "ProcessEngineer", "Supervisor", "Quality"],
    contentVersion: "1.0",
    owner: { "zh-CN": "待指定", en: "Unassigned" },
    reviewedAt: "2026-10-04",
    content: {
      "zh-CN": {
        title: "配方设计与审核",
        summary: "从主配方草稿、工艺校验到多级审核和生效版本的完整路径。",
        keywords: ["主配方", "版本", "工步", "参数槽", "审核链", "电子签名", "导入", "驳回"],
        sections: [
          {
            id: "create",
            title: "创建或导入主配方",
            steps: [
              { title: "核对身份信息", text: "新建时填写配方编码、名称、产品编码和产品名称；先确认目标产品和编码，避免重复创建。" },
              { title: "谨慎导入配方包", text: "导入系统导出的配方包会创建新的草稿 v1，不覆盖已有同编码配方，也不携带审批记录。导入后需在本系统重新校验和审核。" }
            ]
          },
          {
            id: "design",
            title: "设计并保存工艺",
            steps: [
              { title: "维护流程结构", text: "按单元规程配置工步、有向连线及参数槽；删除工步会同时删除相关参数槽和连线。" },
              { title: "先处理校验问题", text: "保存和提交会检查流程非空、拓扑/ISA-88 约束、PLC 时长可写范围，以及质量归档参数的实测来源。" },
              { title: "记录变更并签名", text: "保存工艺需要填写变更原因并完成电子签名。提交审核会先保存当前工艺，再要求单独的提交签名。" }
            ]
          },
          {
            id: "review",
            title: "提交与审核",
            steps: [
              { title: "按当前审核链流转", text: "提交后由审批链当前节点要求的角色决定；同一用户不能为同一版本重复签署不同节点。" },
              { title: "先成功加载差异再通过", text: "待审版与生效版不同时，必须成功加载版本差异才能通过。差异加载中或失败时，“通过”和 Ctrl+Enter 会禁用；可重试加载。驳回不受此差异门控限制。" }
            ],
            note: "审核台显示所有审核中的配方，不代表当前账号都可以签署；以当前节点角色和服务端授权结果为准。"
          }
        ],
        links: [
          { label: "主配方列表", to: "/recipes" },
          { label: "多级审核", to: "/approvals" },
          { label: "审批链配置", to: "/approval-chains" }
        ]
      },
      en: {
        title: "Recipe design and review",
        summary: "Follow a master recipe from draft and process validation through multi-step review to approval.",
        keywords: ["master recipe", "version", "step", "parameter slot", "approval chain", "e-signature", "import", "rejection"],
        sections: [
          {
            id: "create",
            title: "Create or import a master recipe",
            steps: [
              { title: "Verify recipe identity", text: "For a new recipe, enter its code, name, product code, and product name. Confirm the target product and code to avoid duplicates." },
              { title: "Import packages carefully", text: "Importing a system-exported package creates a new draft v1. It does not overwrite an existing recipe with the same code or carry approval records. Validate and review it again in this system." }
            ]
          },
          {
            id: "design",
            title: "Design and save the process",
            steps: [
              { title: "Maintain the process graph", text: "Configure steps, directed links, and parameter slots within unit procedures. Deleting a step also deletes its related slots and links." },
              { title: "Resolve validation issues", text: "Save and submit validate a non-empty process, topology/ISA-88 constraints, writable PLC duration ranges, and measurement sources for quality-archived parameters." },
              { title: "Record the change and sign", text: "Saving the process requires a change reason and e-signature. Submitting for review first saves the current process, then requests a separate submission signature." }
            ]
          },
          {
            id: "review",
            title: "Submit and review",
            steps: [
              { title: "Follow the active review chain", text: "The role required by the current chain step makes the decision. A user cannot sign multiple steps for the same version." },
              { title: "Load the comparison before approval", text: "When the review version differs from the approved version, the comparison must load successfully before approval. Approve and Ctrl+Enter are disabled while loading or after failure; retry is available. Rejection is not gated by this comparison." }
            ],
            note: "The approvals page lists all recipes in review; that does not mean the current account can sign every item. The pending-step role and server authorization decide."
          }
        ],
        links: [
          { label: "Master recipes", to: "/recipes" },
          { label: "Approvals", to: "/approvals" },
          { label: "Approval chains", to: "/approval-chains" }
        ]
      }
    }
  },
  {
    id: "batch-execution",
    category: "execution",
    appliesTo: ["Admin", "Operator", "Supervisor", "ProcessEngineer", "Quality"],
    contentVersion: "1.0",
    owner: { "zh-CN": "待指定", en: "Unassigned" },
    reviewedAt: "2026-10-04",
    content: {
      "zh-CN": {
        title: "批次创建与执行",
        summary: "核对配方快照、单元设备和物料后，按批次状态执行控制。",
        keywords: ["创建批次", "当前批准版本", "设备绑定", "启动", "保持", "恢复", "中止", "跳步", "人工确认"],
        sections: [
          {
            id: "prepare",
            title: "创建前核对",
            steps: [
              { title: "选择主配方", text: "创建表单选择主配方而非版本；系统使用其当前已批准版本生成控制配方快照。创建后核对批次详情中的快照版本。" },
              { title: "绑定兼容设备", text: "多单元配方需为单元绑定兼容设备。设备被占用时可创建快照，但启动需等待设备空闲；同一设备供多个单元使用时会串行执行。" },
              { title: "核对批次物料", text: "提交前核对批次号、缩放因子、产出批号和投料物料批，确保与现场排程和物料状态一致。" }
            ]
          },
          {
            id: "controls",
            title: "运行控制与权限",
            bullets: [
              "操作员或主管可启动、保持、恢复或中止；操作员、主管或质量可确认人工步骤；只有主管可跳步。页面入口仍受批次状态限制。",
              "运行中的保持是签名请求，调度器等待 PLC 确认 PLC_Held 后才暂停；排队批次的保持会直接生效。提交后查看状态和履历。",
              "跳步只针对未完成工步：运行中必须处于安全握手相位；保持或故障批次按离线规则判断；已完成工步不能跳过。"
            ]
          },
          {
            id: "verify",
            title: "操作后确认",
            note: "不要仅凭按钮点击成功推断设备已完成动作。检查批次状态、当前工步、握手摘要和履历；出现故障或状态不一致时停止后续操作并按现场规程处理。"
          }
        ],
        links: [
          { label: "批次列表", to: "/batches" },
          { label: "设备与 PLC", to: "/equipment" }
        ]
      },
      en: {
        title: "Create and execute batches",
        summary: "Verify the recipe snapshot, unit equipment, and materials before controlling a batch.",
        keywords: ["create batch", "current approved version", "equipment assignment", "start", "hold", "resume", "abort", "skip", "manual confirmation"],
        sections: [
          {
            id: "prepare",
            title: "Pre-creation checks",
            steps: [
              { title: "Select a master recipe", text: "The form selects a master recipe, not a version. The system snapshots its currently approved version; verify that snapshot in the batch details after creation." },
              { title: "Assign compatible equipment", text: "Multi-unit recipes need compatible equipment for each unit. A snapshot can be created while equipment is occupied, but starting must wait until it is free. Shared equipment runs units serially." },
              { title: "Verify batch materials", text: "Check the batch number, scale factor, output lot, and input material lots against the production schedule and material state." }
            ]
          },
          {
            id: "controls",
            title: "Controls and roles",
            bullets: [
              "Operators or supervisors can start, hold, resume, or abort; operators, supervisors, or Quality can confirm a manual step; only supervisors can skip. Actions also depend on batch state.",
              "A hold during execution is a signed request; the scheduler waits for PLC_Held before pausing. A queued batch is held directly. Check state and history after submitting.",
              "Skip applies only to unfinished steps: a running batch requires a safe handshake phase; held or faulted batches use the offline rule; completed steps cannot be skipped."
            ]
          },
          {
            id: "verify",
            title: "Verify the result",
            note: "Do not infer that the equipment completed an action from a button click alone. Check batch state, current step, handshake summary, and history. Stop and follow site procedure if a fault or inconsistent state appears."
          }
        ],
        links: [
          { label: "Batch execution", to: "/batches" },
          { label: "Equipment & PLC", to: "/equipment" }
        ]
      }
    }
  },
  {
    id: "quality-release",
    category: "quality",
    appliesTo: ["Admin", "Supervisor", "Quality"],
    contentVersion: "1.0",
    owner: { "zh-CN": "待指定", en: "Unassigned" },
    reviewedAt: "2026-10-04",
    content: {
      "zh-CN": {
        title: "批记录、样品与质量放行",
        summary: "审核电子批记录证据，处理实验室样品，再执行放行或拒收。",
        keywords: ["电子批记录", "PDF", "Final", "实验室样品", "放行", "拒收", "EVIDENCE_STALE", "证据摘要"],
        sections: [
          {
            id: "record",
            title: "查看批记录",
            bullets: [
              "电子批记录和 PDF 仅主管、质量和管理员可查看；先核对批次、配方快照、工步、投料和质量归档。",
              "页面仅在批次完成后向质量角色显示样品判定操作；样品必须仍为待判状态。不合格判定需填写说明。"
            ]
          },
          {
            id: "release-gates",
            title: "放行前置条件",
            steps: [
              { title: "处理待判终检样品", text: "所有待判终检（Final）样品必须先完成判定。放行前核对实验室样品判定签名完整性；Mismatch 或 Unsupported 会阻止放行。" },
              { title: "说明质量异常", text: "归档质检结果超差、缺少必需实测归档数据或存在不合格样品时，放行意见必须说明理由。" },
              { title: "拒收时记录原因", text: "拒收操作需要填写可追溯的拒收原因；提交前复核目标批次和结论。" }
            ]
          },
          {
            id: "evidence",
            title: "签署与证据过期",
            note: "放行和拒收都绑定当前批记录证据摘要。若提交返回 409 EVIDENCE_STALE，说明证据已变化；刷新页面、重新核对完整批记录后再签，不要重复提交旧摘要。"
          }
        ],
        links: [
          { label: "批次列表", to: "/batches" },
          { label: "操作审计", to: "/audit" }
        ]
      },
      en: {
        title: "Batch records, samples, and release",
        summary: "Review electronic batch-record evidence, disposition lab samples, then release or reject.",
        keywords: ["electronic batch record", "PDF", "Final", "lab sample", "release", "reject", "EVIDENCE_STALE", "evidence digest"],
        sections: [
          {
            id: "record",
            title: "Review the batch record",
            bullets: [
              "The electronic batch record and PDF are available only to supervisors, Quality, and administrators. Verify the batch, recipe snapshot, steps, materials, and quality archive.",
              "The page shows sample-disposition actions to Quality users only after batch completion. A sample must still be pending; a failed disposition requires an explanation."
            ]
          },
          {
            id: "release-gates",
            title: "Release prerequisites",
            steps: [
              { title: "Resolve pending final samples", text: "All pending Final samples must be dispositioned first. Check lab-sample disposition-signature integrity before release; Mismatch or Unsupported blocks release." },
              { title: "Explain quality exceptions", text: "If archived quality results are out of specification, required measured archive data is missing, or a sample failed, the release comment must explain the rationale." },
              { title: "Record rejection rationale", text: "Rejection requires a traceable reason. Verify the target batch and disposition before submitting." }
            ]
          },
          {
            id: "evidence",
            title: "Signature and stale evidence",
            note: "Release and rejection bind the current batch-record evidence digest. A 409 EVIDENCE_STALE response means the evidence changed; refresh, review the complete record, and sign again instead of resubmitting an old digest."
          }
        ],
        links: [
          { label: "Batch execution", to: "/batches" },
          { label: "Audit trail", to: "/audit" }
        ]
      }
    }
  },
  {
    id: "material-lots",
    category: "materials",
    appliesTo: ["Admin", "Operator", "Supervisor", "Quality"],
    contentVersion: "1.0",
    owner: { "zh-CN": "待指定", en: "Unassigned" },
    reviewedAt: "2026-10-04",
    content: {
      "zh-CN": {
        title: "物料批登记与谱系",
        summary: "登记来料批，追踪拆分、投料和产出关系。",
        keywords: ["物料批", "来料", "数量", "检验状态", "拆分", "母批", "子批", "谱系"],
        sections: [
          {
            id: "register",
            title: "登记来料批",
            steps: [
              { title: "核对批次身份", text: "登记表填写物料编码、批号、数量和单位；检验状态不在此表单登记，新批状态由系统设置为 Open（在库）。" },
              { title: "检查重复与单位", text: "提交前确认批号、物料编码和数量单位与收货凭证一致；发生校验错误时按服务端提示修正。" }
            ]
          },
          {
            id: "split",
            title: "拆分子批",
            steps: [
              { title: "确认状态和权限", text: "当前批为 Open 时页面显示拆分入口；仅管理员、操作员、主管和质量角色可提交，入口可见不代表已获授权。" },
              { title: "核对数量影响", text: "提交会创建子批，并从母批数量中扣减拆分数量。提交前核对父批、子批批号、数量和单位。" }
            ],
            note: "服务端还会检查剩余数量、批号唯一性等约束；页面表单的可见信息不是全部校验。"
          },
          {
            id: "trace",
            title: "沿谱系追溯",
            bullets: [
              "从母批、子批和当前批次之间的链接核对拆分关系；从批次记录确认实际投料与产出。",
              "发现数量或批号不一致时，先暂停后续物料操作并按现场物料流程核查。"
            ]
          }
        ],
        links: [
          { label: "物料批次列表", to: "/lots" },
          { label: "批次执行", to: "/batches" }
        ]
      },
      en: {
        title: "Material lots and genealogy",
        summary: "Register incoming lots and trace splits, consumption, and production relationships.",
        keywords: ["material lot", "receiving", "quantity", "inspection status", "split", "parent lot", "child lot", "genealogy"],
        sections: [
          {
            id: "register",
            title: "Register an incoming lot",
            steps: [
              { title: "Verify lot identity", text: "The form records material code, lot number, quantity, and unit; inspection status is not recorded here. The system sets a new lot to Open." },
              { title: "Check identity and unit", text: "Compare the lot number, material code, quantity, and unit with receiving documents. Correct any validation error reported by the server." }
            ]
          },
          {
            id: "split",
            title: "Split a child lot",
            steps: [
              { title: "Check state and authorization", text: "The page shows the split action for an Open lot, but only Admin, Operator, Supervisor, and Quality can submit it. Visibility does not guarantee authorization." },
              { title: "Verify the quantity impact", text: "Submitting creates a child lot and subtracts the split quantity from the parent. Verify the parent, child lot number, quantity, and unit first." }
            ],
            note: "The server also validates remaining quantity and lot-number uniqueness; the form does not show every check."
          },
          {
            id: "trace",
            title: "Trace genealogy",
            bullets: [
              "Follow links between parent, child, and current lots to verify splits; use batch records to confirm actual consumption and production.",
              "If quantities or lot numbers do not match, pause further material handling and investigate under site procedure."
            ]
          }
        ],
        links: [
          { label: "Material lots", to: "/lots" },
          { label: "Batch execution", to: "/batches" }
        ]
      }
    }
  },
  {
    id: "equipment-setup",
    category: "materials",
    appliesTo: ["Admin", "Operator", "Supervisor", "ProcessEngineer"],
    contentVersion: "1.0",
    owner: { "zh-CN": "待指定", en: "Unassigned" },
    reviewedAt: "2026-10-04",
    content: {
      "zh-CN": {
        title: "设备、PLC 与相模板",
        summary: "登记设备连接与点表，维护相模板，并理解验证能力边界。",
        keywords: ["设备", "PLC", "握手", "点表", "Modbus", "OPC UA", "Simulator", "相库", "相模板"],
        sections: [
          {
            id: "configure",
            title: "先登记设备与点表",
            bullets: [
              "设备编码创建后不可修改；连接参数、设备类、握手点表、实测点和写参槽必须与现场点表一致。",
              "“测试连接”只验证连接尝试，不代表握手、所有地址或配方可执行性已验证；点表验证也不能替代受控现场试运行。",
              "仿真故障入口仅用于 Simulator，不应用于真实 PLC。"
            ]
          },
          {
            id: "templates",
            title: "维护设备类相模板",
            steps: [
              { title: "按设备类维护模板", text: "相模板为新建配方工步提供初始程序号、看门狗和参数槽值；模板内容不会自动回写已有配方工步。" },
              { title: "核对程序与写入地址", text: "区分 PLC 执行的相与上位机工步；按现场 PLC 程序号范围和点表核对写参槽地址后再用于生产。" }
            ]
          },
          {
            id: "validate",
            title: "投用前验证",
            note: "先在受控环境验证握手信号、超时/复位路径和参数写入，再按现场变更流程投用。连接成功不等于工艺安全验证完成。"
          }
        ],
        links: [
          { label: "设备与 PLC", to: "/equipment" },
          { label: "主配方设计", to: "/recipes" }
        ]
      },
      en: {
        title: "Equipment, PLC, and phase templates",
        summary: "Configure equipment connections and point maps, maintain phase templates, and understand validation limits.",
        keywords: ["equipment", "PLC", "handshake", "tag map", "Modbus", "OPC UA", "Simulator", "phase library", "phase template"],
        sections: [
          {
            id: "configure",
            title: "Register equipment and point maps",
            bullets: [
              "An equipment code cannot be changed after creation. Connection settings, equipment class, handshake map, measured points, and write slots must match the plant tag map.",
              "Test connection verifies only a connection attempt; it does not validate the handshake, every address, or recipe executability. Point-map validation does not replace a controlled site trial.",
              "The simulated-fault action is for Simulator equipment only, never a real PLC."
            ]
          },
          {
            id: "templates",
            title: "Maintain class-based templates",
            steps: [
              { title: "Maintain templates by equipment class", text: "A phase template seeds program number, watchdog, and parameter-slot values for a new recipe step. Later template changes do not update saved steps." },
              { title: "Verify program and write addresses", text: "Distinguish PLC phases from host-computer steps. Check program-number ranges and parameter-slot addresses against the plant map before production use." }
            ]
          },
          {
            id: "validate",
            title: "Validate before use",
            note: "In a controlled environment, verify handshake signals, timeout/reset paths, and parameter writes before release under the site change procedure. A successful connection is not process-safety validation."
          }
        ],
        links: [
          { label: "Equipment & PLC", to: "/equipment" },
          { label: "Master recipes", to: "/recipes" }
        ]
      }
    }
  },
  {
    id: "alarms-troubleshooting",
    category: "system",
    appliesTo: ["Admin", "Operator", "Supervisor", "Quality"],
    contentVersion: "1.0",
    owner: { "zh-CN": "待指定", en: "Unassigned" },
    reviewedAt: "2026-10-04",
    content: {
      "zh-CN": {
        title: "报警处理与追溯",
        summary: "先处置报警原因，再确认记录；必要时用审计记录还原操作。",
        keywords: ["报警", "确认", "未确认", "审计", "时间筛选", "故障"],
        sections: [
          {
            id: "alarms",
            title: "处置过程报警",
            steps: [
              { title: "先定位原因", text: "结合报警等级、设备、工步、批次及报警详情判断影响；需要时进入批次监控或设备页检查现场状态。" },
              { title: "确认不是故障复位", text: "管理员、操作员、主管和质量可查看报警；只有操作员、主管和质量可确认。确认报警只记录已知悉，不会自动清除故障或恢复设备。" }
            ]
          },
          {
            id: "audit",
            title: "查询操作审计",
            bullets: [
              "审计页仅质量和管理员可访问；可按用户、动作、详情关键字和实体类型查找，列表按时间倒序分页。",
              "当前页面没有时间范围筛选，也不提供修改/删除审计记录的操作。按批次、配方或设备链接继续核对业务记录。"
            ]
          },
          {
            id: "escalate",
            title: "无法安全恢复时",
            note: "若报警仍在触发、批次状态与 PLC 握手不一致，或现场安全条件不明确，不要重复确认或强制启动；按现场 SOP 升级给主管/维护人员。"
          }
        ],
        links: [
          { label: "过程报警", to: "/alarms" },
          { label: "操作审计", to: "/audit" }
        ]
      },
      en: {
        title: "Alarm handling and traceability",
        summary: "Resolve the alarm cause before acknowledging it; use the audit trail when an action needs investigation.",
        keywords: ["alarm", "acknowledge", "open alarm", "audit", "time filter", "fault"],
        sections: [
          {
            id: "alarms",
            title: "Handle process alarms",
            steps: [
              { title: "Identify the cause first", text: "Use severity, equipment, step, batch, and details to assess impact. Open the batch monitor or equipment page when the live state needs inspection." },
              { title: "Acknowledgement is not reset", text: "Admins, Operators, Supervisors, and Quality users can view alarms; only Operators, Supervisors, and Quality users can acknowledge them. Acknowledgement records awareness; it does not clear a fault or recover equipment." }
            ]
          },
          {
            id: "audit",
            title: "Search the audit trail",
            bullets: [
              "The audit page is restricted to Quality and Admin users. Search by user, action, detail keyword, or entity type; results are paginated in descending time order.",
              "There is no time-range filter and no edit/delete action for audit records on this page. Follow batch, recipe, or equipment links to inspect the business record."
            ]
          },
          {
            id: "escalate",
            title: "When safe recovery is unclear",
            note: "If an alarm remains active, batch state disagrees with the PLC handshake, or site safety is uncertain, do not repeatedly acknowledge or force a start. Escalate to a supervisor or maintenance staff under site SOP."
          }
        ],
        links: [
          { label: "Process alarms", to: "/alarms" },
          { label: "Audit trail", to: "/audit" }
        ]
      }
    }
  },
  {
    id: "accounts-backup",
    category: "system",
    appliesTo: ["Admin"],
    contentVersion: "1.0",
    owner: { "zh-CN": "待指定", en: "Unassigned" },
    reviewedAt: "2026-10-04",
    content: {
      "zh-CN": {
        title: "账号、备份与数据库维护",
        summary: "管理员维护账号权限，理解服务器快照、整库下载和维护操作的边界。",
        keywords: ["用户", "角色", "管理员", "备份", "快照", "恢复", "SQLite", "VACUUM", "维护", "下载数据库"],
        sections: [
          {
            id: "accounts",
            title: "维护账号",
            bullets: [
              "每个账号分配一个角色；只授予岗位所需权限。不能停用当前登录账号或最后一个启用的管理员。",
              "离岗账号按组织流程停用，不删除其既有审计记录。"
            ]
          },
          {
            id: "snapshots",
            title: "服务器快照与保留",
            bullets: [
              "备份列表显示日常服务器快照，不显示 pre-migration 升级前快照。计划时刻为 UTC；下次执行和文件时间按浏览器本地时区显示。",
              "快照完整性检查通过后，正数保留份数才会裁剪超额旧快照；保留数为 0 或负数时不裁剪。",
              "快照位于服务器，页面没有数据库恢复或恢复演练入口。快照文件存在或完整性检查通过，不等于已验证可恢复。"
            ]
          },
          {
            id: "maintenance",
            title: "维护与整库下载",
            steps: [
              { title: "手动维护不会先备份", text: "手动维护先执行 PRAGMA optimize；只有数据库空闲且符合配置阈值时才执行 VACUUM。维护前确认近期快照并通过独立恢复演练验证恢复能力。" },
              { title: "保护整库文件", text: "每次下载完整 SQLite 数据库都需要管理员电子签名。文件包含敏感数据库数据，需按组织安全策略保管和传输。" }
            ]
          }
        ],
        links: [
          { label: "用户与备份", to: "/users" },
          { label: "操作审计", to: "/audit" }
        ]
      },
      en: {
        title: "Accounts, backups, and database maintenance",
        summary: "Manage account access and understand the limits of server snapshots, full-database downloads, and maintenance.",
        keywords: ["users", "roles", "administrator", "backup", "snapshot", "restore", "SQLite", "VACUUM", "maintenance", "database download"],
        sections: [
          {
            id: "accounts",
            title: "Manage accounts",
            bullets: [
              "Assign one role per account and grant only job-required permissions. The current account and the last enabled administrator cannot be disabled.",
              "Disable departed accounts under organizational procedure; do not delete their existing audit records."
            ]
          },
          {
            id: "snapshots",
            title: "Server snapshots and retention",
            bullets: [
              "The backup list shows daily server snapshots, not pre-migration snapshots. The schedule time is UTC; next-run and file timestamps use the browser's local time zone.",
              "After an integrity check passes, excess snapshots are pruned only when the retention count is positive; zero or a negative value disables pruning.",
              "Snapshots stay on the server, and this page has no restore or recovery-test action. File presence or a passing integrity check does not prove recoverability."
            ]
          },
          {
            id: "maintenance",
            title: "Maintenance and full-database download",
            steps: [
              { title: "Manual maintenance does not back up first", text: "Manual maintenance runs PRAGMA optimize; VACUUM runs only when the database is idle and configured thresholds are met. Confirm a recent snapshot and verify recovery through a separate restore test first." },
              { title: "Protect the full database file", text: "Every full SQLite database download requires an administrator e-signature. The file contains sensitive database data and must be stored and transferred under security policy." }
            ]
          }
        ],
        links: [
          { label: "Users & backup", to: "/users" },
          { label: "Audit trail", to: "/audit" }
        ]
      }
    }
  }
];

export const PAGE_GUIDE_ARTICLE_BY_KEY: Readonly<Record<string, string>> = {
  dashboard: "start-here",
  recipes: "recipe-lifecycle",
  recipeDesigner: "recipe-lifecycle",
  approvals: "recipe-lifecycle",
  approvalChains: "recipe-lifecycle",
  batches: "batch-execution",
  batchMonitor: "batch-execution",
  batchRecord: "quality-release",
  materialLots: "material-lots",
  lotGenealogy: "material-lots",
  alarms: "alarms-troubleshooting",
  equipment: "equipment-setup",
  audit: "alarms-troubleshooting",
  users: "accounts-backup",
  notFound: "start-here"
};
