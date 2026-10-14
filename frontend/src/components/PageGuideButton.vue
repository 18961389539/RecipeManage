<template>
  <el-tooltip :content="labels.button" placement="top">
    <button
      type="button"
      class="page-guide-trigger"
      :aria-label="labels.button"
      :title="labels.button"
      @click="open = true"
    >?</button>
  </el-tooltip>
  <el-dialog
    v-model="open"
    :title="guide.title"
    width="min(720px, calc(100vw - 32px))"
    class="page-guide-dialog"
    :append-to-body="true"
  >
    <div class="page-guide-content">
      <p>{{ guide.purpose }}</p>
      <h3 class="page-guide-section">{{ labels.steps }}</h3>
      <ol class="page-guide-steps">
        <li v-for="(step, index) in guide.steps" :key="index">
          <span class="page-guide-step-index">{{ String(index + 1).padStart(2, "0") }}</span>
          <span>{{ step }}</span>
        </li>
      </ol>
      <section v-for="section in details" :key="section.title" class="page-guide-detail">
        <h3 class="page-guide-section">{{ section.title }}</h3>
        <ul class="page-guide-list">
          <li v-for="(item, index) in section.items" :key="index">{{ item }}</li>
        </ul>
      </section>
      <div v-if="guide.note" class="page-guide-note">
        <strong>{{ labels.note }}：</strong>{{ guide.note }}
      </div>
      <router-link v-if="fullArticlePath" class="page-guide-full-link" :to="fullArticlePath">
        {{ labels.fullGuide }} <span aria-hidden="true">→</span>
      </router-link>
    </div>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref } from "vue";
import { useI18n } from "vue-i18n";
import { useRouter } from "vue-router";
import { PAGE_GUIDE_ARTICLE_BY_KEY } from "../help/catalog";

interface GuideCopy {
  title: string;
  purpose: string;
  steps: readonly string[];
  note?: string;
}

interface GuideSection {
  title: string;
  items: readonly string[];
}

const PAGE_GUIDES = {
  dashboard: {
    "zh-CN": {
      title: "运行总览说明",
      purpose: "集中查看待处理批次、未确认报警、设备占用和在途执行。",
      steps: ["先查看当前角色可见的待处理卡片及未确认报警；点击计数进入对应列表。", "检查设备占用行；只有关联批次的行可打开批次。", "在在途批次查看工步、时长与握手阶段。"]
    },
    en: {
      title: "Dashboard guide",
      purpose: "Review batches needing attention, open alarms, equipment occupancy, and active runs.",
      steps: ["Start with actionable cards and unacknowledged alarms available to your role; select a count to open its list.", "Select an equipment row with a linked batch to open that batch.", "Review the current step, elapsed time, and handshake phase in active batches."]
    }
  },
  recipes: {
    "zh-CN": {
      title: "主配方说明",
      purpose: "查询、筛选和维护主配方及其单元规程。",
      steps: ["按编码、名称或产品搜索配方。", "使用状态筛选定位草稿、审核中或已生效配方。", "打开配方进入设计；新建时核对产品信息，导入前核对配方包中的产品信息。"]
    },
    en: {
      title: "Master recipes guide",
      purpose: "Find, filter, and maintain master recipes and their unit procedures.",
      steps: ["Search by code, name, or product.", "Filter drafts, items in review, or approved recipes.", "Open a recipe to design it; verify product details when creating or importing a package."]
    }
  },
  recipeDesigner: {
    "zh-CN": {
      title: "配方设计说明",
      purpose: "维护配方的工艺结构、工步参数和审核提交内容。",
      steps: ["编辑单元规程、工步顺序及参数。", "保存前检查校验结果和版本差异；保存需要填写变更原因并签名。", "提交审核会先保存当前工艺，再要求提交签名；未保存标记消失前不要离开。"],
      note: "审核中的版本可在审核工作台处理；设计器也会向当前待审节点所需角色提供通过/驳回操作。"
    },
    en: {
      title: "Recipe designer guide",
      purpose: "Maintain recipe structure, step parameters, and review submissions.",
      steps: ["Edit unit procedures, step order, and parameters.", "Check validation results and version differences; saving requires a change reason and signature.", "Submitting for review first saves the current procedure, then requests a submission signature; do not leave while the unsaved marker remains."],
      note: "The approvals workspace handles in-review versions; the designer also offers approve/reject actions to the role required by the current step."
    }
  },
  approvals: {
    "zh-CN": {
      title: "多级审核说明",
      purpose: "查看审核中的配方，并由当前待审节点要求的角色处理审核决定。",
      steps: ["打开待审核配方，核对版本和差异。", "确认当前节点的审核意见与电子签名要求。", "提交通过或驳回决定后，继续处理下一项。"],
      note: "只能处理审批链中当前分配给自己的节点。"
    },
    en: {
      title: "Recipe approvals guide",
      purpose: "View recipes in review and let the role required by the current pending step submit its decision.",
      steps: ["Open a pending recipe and verify its version and differences.", "Check the current review step and e-signature requirements.", "Submit an approval or rejection, then continue with the next item."],
      note: "You can act only on the approval step currently assigned to you."
    }
  },
  approvalChains: {
    "zh-CN": {
      title: "审批链配置说明",
      purpose: "配置配方提交后的审核顺序、节点角色及通过/驳回时的签署含义。",
      steps: ["选择已有审批链，或创建一条新链。", "按顺序配置审核节点、角色和节点提示。", "保存后检查配方使用的审批链是否正确。"],
      note: "修改审批链只影响之后提交的版本；在审版本的节点保持提交时的配置。"
    },
    en: {
      title: "Approval chain setup guide",
      purpose: "Configure review order, required roles, and signature meanings for approval or rejection.",
      steps: ["Select an existing chain or create a new one.", "Set review steps, roles, and prompts in order.", "After saving, verify the chain selected by each recipe."],
      note: "Changes apply to future submissions; in-review versions keep the chain captured at submission."
    }
  },
  batches: {
    "zh-CN": {
      title: "生产批次说明",
      purpose: "查询生产批次，并跟踪从配方创建到执行、放行的进度。",
      steps: ["按批次号、产品名、设备编码或主配方名称搜索，并按状态筛选。", "查看批次状态和执行信息，打开记录进入实时监控。", "创建批次时选择已批准的主配方并核对设备；系统使用其当前已批准版本生成快照。"]
    },
    en: {
      title: "Production batches guide",
      purpose: "Find production batches and follow progress from recipe selection through execution and release.",
      steps: ["Search by batch number, product name, equipment code, or master-recipe name, then filter by status.", "Review batch status and open a record for live monitoring.", "When creating a batch, select an approved recipe and verify the equipment; the system snapshots its currently approved version."]
    }
  },
  batchMonitor: {
    "zh-CN": {
      title: "批次实时监控说明",
      purpose: "跟踪单个批次的实时状态、工步、设备握手、报警和过程数据。",
      steps: ["先核对批次、配方快照、设备和当前状态。", "按页面提示完成确认、保持或恢复等获授权操作。", "执行完成后打开电子批记录，核对记录并办理质量放行。"],
      note: "运行控制会影响实际批次；仅在确认设备和批次无误后操作。"
    },
    en: {
      title: "Batch monitor guide",
      purpose: "Track one batch's live status, step, equipment handshake, alarms, and process data.",
      steps: ["Verify the batch, recipe snapshot, equipment, and current status.", "Use the available controls for authorized confirmation, hold, or resume actions.", "After execution, open the electronic batch record for review and quality release."],
      note: "Run controls affect the active batch; verify the batch and equipment before acting."
    }
  },
  batchRecord: {
    "zh-CN": {
      title: "电子批记录说明",
      purpose: "核对批次执行过程、质量数据、物料谱系和电子签名记录。",
      steps: ["检查配方快照、工步结果及设备握手记录。", "核对物料、质检样品、报警和审批签名。", "由具备权限的质量人员完成放行或拒收。"],
      note: "归档记录用于追溯；处置前请核对批次与快照信息。"
    },
    en: {
      title: "Electronic batch record guide",
      purpose: "Review batch execution, quality data, material genealogy, and e-signature records.",
      steps: ["Check the recipe snapshot, step results, and equipment handshake records.", "Review materials, lab samples, alarms, and approval signatures.", "An authorized quality user completes release or rejection."],
      note: "This record supports traceability; verify the batch and snapshot before disposition."
    }
  },
  materialLots: {
    "zh-CN": {
      title: "物料批次谱系说明",
      purpose: "登记来料批次，并查询物料批在生产中的流转关系。",
      steps: ["按批号或物料搜索来料批。", "登记时核对物料编码、批号、数量和单位；检验状态不在此表单登记。", "打开物料批查看子批拆分和投料去向。"]
    },
    en: {
      title: "Material lots guide",
      purpose: "Register incoming material lots and trace their movement through production.",
      steps: ["Search incoming lots by lot number or material.", "When registering a lot, verify its material code, lot number, quantity, and unit; inspection status is not recorded in this form.", "Open a material lot to review splits and consumption destinations."]
    }
  },
  lotGenealogy: {
    "zh-CN": {
      title: "物料谱系说明",
      purpose: "从当前物料批向上追溯来源，向下查看拆分和投料去向。",
      steps: ["检查当前批次的物料、状态和来源。", "查看祖先批与子批，沿链接切换谱系节点；拆分会扣减母批数量并创建子批。", "仅有拆分权限的角色可提交拆分；核对生产批次及投料关系后返回列表。"]
    },
    en: {
      title: "Material genealogy guide",
      purpose: "Trace a material lot upstream to its source and downstream to splits and consumption.",
      steps: ["Verify the current lot's material, status, and source.", "Follow ancestor and child lots through the genealogy links; splitting creates a child lot and subtracts its quantity from the parent.", "Only authorized roles can submit a split. Confirm production batch and consumption relationships, then return to the list."]
    }
  },
  alarms: {
    "zh-CN": {
      title: "过程报警说明",
      purpose: "查看握手故障、质检超差和调度异常，并跟踪确认状态。",
      steps: ["优先筛选未确认报警并查看关联批次。", "根据报警说明核对设备、工步或质检数据。", "问题按流程处置后再确认；确认权限仅开放给操作员、主管和质量角色。"]
    },
    en: {
      title: "Process alarms guide",
      purpose: "Review handshake faults, out-of-range quality results, and scheduling exceptions.",
      steps: ["Filter open alarms first and inspect the associated batch.", "Use the alarm details to verify equipment, step, or quality data.", "Acknowledge only after the issue is handled; acknowledgement is limited to Operators, Supervisors, and Quality users."]
    }
  },
  equipment: {
    "zh-CN": {
      title: "设备与 PLC 驱动说明",
      purpose: "维护设备连接、PLC 四步握手点表与按设备类组织的 PLC 工艺相模板，并在配方设计时复用模板。",
      steps: [
        "在“设备”页签按编码、名称、主机、型号、设备类、协议或占用批次搜索；编辑现有设备时编码不可修改。",
        "核对协议、端点、设备类和现场点表后保存；再分别使用“测试连接”和“校验点表”检查配置。",
        "在“相库”页签展开目标设备类维护写 PLC 的相模板；配方设计器需从对应设备类的相库添加工步。"
      ],
      note: "相模板用于生成新的配方工步，编辑或删除模板不会回写已保存工步。等待、质检、人工确认属于上位机工步，不在相库中。"
    },
    en: {
      title: "Equipment and PLC guide",
      purpose: "Maintain equipment connections, PLC four-step handshake maps, and PLC process-phase templates by equipment class for reuse in recipes.",
      steps: [
        "On Equipment, search by code, name, host, model, equipment class, protocol, or occupying batch. An existing equipment code cannot be edited.",
        "Verify the protocol, endpoint, equipment class, and approved point map before saving; then use Test connection and Validate point map for their respective checks.",
        "On Phase library, expand the target equipment class to maintain PLC-writing templates. The recipe designer adds steps from the matching class library."
      ],
      note: "A phase template seeds new recipe steps; editing or deleting it does not rewrite saved steps. Wait, quality, and manual-confirm steps are host-side steps and are not stored in this library."
    }
  },
  audit: {
    "zh-CN": {
      title: "操作审计说明",
      purpose: "按用户、动作、实体和关键字查询关键操作留痕；本页不提供时间范围筛选。",
      steps: ["按用户、动作或详情关键字搜索，并按实体类型筛选。", "检查时间倒序列表，打开可跳转记录核对关联对象。", "结合批次、配方或设备页面还原操作闭环。"],
      note: "审计记录用于追溯，不可在此页面修改。"
    },
    en: {
      title: "Audit log guide",
      purpose: "Find significant actions by user, action, entity, and keyword; this page has no time-range filter.",
      steps: ["Search by user, action, or detail keyword and filter by entity type.", "Review the time-descending list and open linked entries to verify related objects.", "Use batch, recipe, or equipment pages to reconstruct the operation context."],
      note: "Audit entries are for traceability and cannot be edited here."
    }
  },
  users: {
    "zh-CN": {
      title: "用户与备份说明",
      purpose: "维护用户账号与角色，查看服务器每日 SQLite 快照，并手动备份、维护或下载整库。",
      steps: ["创建或编辑账号时核对登录名与角色；每个账号分配一个角色。", "按岗位授予最小必要权限；不能停用当前账号或最后一个启用的管理员。", "维护前确认近期快照，并通过独立恢复演练确认可恢复性；手动维护本身不会先创建备份。"],
      note: "此页面仅管理员可访问；数据库文件应按安全要求保管。"
    },
    en: {
      title: "Users and backups guide",
      purpose: "Manage user accounts and roles, review daily SQLite snapshots on the server, and manually back up, maintain, or download the database.",
      steps: ["Verify the login name and role when creating or editing an account; each account has one role.", "Grant only job-required permissions. The current account and the last enabled administrator cannot be disabled.", "Before maintenance, confirm a recent snapshot and verify recovery separately; manual maintenance does not create a backup first."],
      note: "This page is restricted to administrators; protect database files according to security policy."
    }
  },
  login: {
    "zh-CN": {
      title: "登录说明",
      purpose: "使用已分配的账号进入工艺配方管理与实时执行系统。",
      steps: ["输入用户名和密码后选择登录。", "登录成功后返回可接受的站内地址；缺失或不接受时进入运行总览。不存在的路径显示页面不存在，受限路径提示后返回总览。", "登录失败时检查凭证并联系管理员核对账号是否启用；功能权限在登录后另行检查。"],
      note: "不要与他人共享密码。"
    },
    en: {
      title: "Sign-in guide",
      purpose: "Use your assigned account to access the recipe and execution system.",
      steps: ["Enter your username and password, then select Sign in.", "After sign-in, the app returns to an accepted in-app path; otherwise it opens the dashboard. Unmatched paths show Page not found; restricted routes warn and return to the dashboard.", "If sign-in fails, check your credentials and ask an administrator to verify that the account is enabled; feature permissions are checked after sign-in."],
      note: "Do not share your password."
    }
  },
  notFound: {
    "zh-CN": {
      title: "页面不存在说明",
      purpose: "当前地址没有匹配到应用路由；这不表示角色权限不足。",
      steps: ["检查地址拼写、批次或资源 ID，以及书签或分享链接是否过期。", "已知功能但权限不足时，系统会提示并返回运行总览；此页只表示地址未匹配路由。", "使用侧边栏或返回运行总览后重新导航。"]
    },
    en: {
      title: "Page not found guide",
      purpose: "No application route matches this address; this does not indicate a role-permission failure.",
      steps: ["Check the address, batch or resource ID, and whether a bookmark or shared link is outdated.", "A known feature blocked by permissions displays a warning and returns to the dashboard; this page means the route did not match.", "Use the sidebar or return to the dashboard and navigate again."]
    }
  }
} as const;

const PAGE_GUIDE_DETAILS = {
  dashboard: {
    "zh-CN": [
      { title: "查看顺序", items: ["先看当前角色可见的待处理卡片：故障、保持、待放行、待检终样及适用角色的待审核项；报警入口也按角色显示。点击计数进入对应列表。", "再看设备占用表：占用状态、批次状态和握手阶段分别描述设备当前占用、关联批次进度及设备交互阶段；只有关联批次的行可进入批次详情。", "最后看在途批次表，结合批次状态、工步、时长、握手阶段和设备识别卡住或等待中的任务；点击行可进入监控。"] },
      { title: "数字与数据状态", items: ["上方“需处理”是优先处置入口；“参考”数字用于态势观察，不应与待办项混为一谈。待审核卡片仅 Supervisor/Quality 可见；报警入口仅 Admin、Operator、Supervisor、Quality 可见。", "执行态势显示最近 2 小时内最多 9 条重要事件，并非完整事件流；只有关联批次的事件可跳转。", "数字显示“—”表示数据未知或尚未成功取得，不代表数量为零。取数失败时，若有上次成功数据，页面会明确提示其已过期；正常情况下页面会自动刷新。"] },
      { title: "筛选与跳转", items: ["设备数量较多时可启用“只看非空闲”，快速收敛到正在运行、排队、保持或故障相关设备。", "点击 KPI、报警条、设备占用行、在途批次行或关联事件，跳到相应处置页面；返回总览继续核对整体状态。"] }
    ],
    en: [
      { title: "Review sequence", items: ["Start with actionable cards available to your role: faulted, held, pending-release, pending-final-sample, and applicable review items. Alarm entry points are also role-gated; select a count to open its list.", "Next, review equipment occupancy: occupancy, batch status, and handshake phase describe equipment use, batch progress, and the interaction phase respectively. Only rows linked to a batch open its details.", "Then inspect active batches. Read status, step, elapsed time, handshake phase, and equipment together to identify work that may be waiting or stalled; select a row to open its monitor."] },
      { title: "Counts and data state", items: ["Actionable cards are the priority queue; the reference strip is for situational awareness and should not be confused with pending work. Pending approvals are shown only to Supervisor/Quality; alarm entry points are shown only to Admin, Operator, Supervisor, and Quality.", "Execution events show up to nine significant events from the last two hours, not the complete event stream. Only events linked to a batch can be opened.", "A dash means the data is unknown or has not loaded, not zero. If retrieval fails after a successful load, the page marks the retained values as stale; normal refresh is automatic."] },
      { title: "Filters and navigation", items: ["When many equipment rows are present, enable Only busy to focus on equipment that is running, queued, held, or faulted.", "Select a KPI, alarm strip, occupied-equipment row, active-batch row, or linked event to open its relevant workflow; return here to recheck overall status."] }
    ]
  },
  recipes: {
    "zh-CN": [
      { title: "查找与筛选", items: ["在搜索框输入配方编码、名称或产品关键字；使用状态筛选缩小结果。", "“草稿、审核中、驳回”根据草稿状态筛选；“已生效”表示存在已批准版本。生效版本与当前草稿/待审版本可能不同。", "表格中的“生效版本”“草稿状态”“待审节点”是不同信息；不要仅凭其中一列判断另一个版本的审核结果。"] },
      { title: "查看与维护", items: ["选择配方行进入设计器，查看版本、工艺结构、参数及审批状态。", "有权限时可新建或导入 JSON；新建时填写编码、名称、产品编码和产品名称。导入前核对系统导出的配方包及包内产品信息；导入会从包内选取版本创建新草稿 v1，不覆盖同编码配方，也不继承审批记录，需在本系统重新审核。", "按权限使用导出；分享导出文件前确认其内容和版本适合接收者。"] },
      { title: "结果边界", items: ["编码重复提示取决于当前已加载数据，最终唯一性以保存时服务端校验为准。", "导入前确认文件来源和目标配方；页面入口本身不能保证导入内容与当前工艺兼容。"] }
    ],
    en: [
      { title: "Find and filter", items: ["Search by recipe code, name, or product keyword, then use the status filter to narrow the list.", "Draft, In review, and Rejected filter by draft status; Approved means an approved version exists. The approved version may differ from the current draft or submitted version.", "Approved version, draft status, and pending review step are separate facts. Do not infer one from another."] },
      { title: "Review and maintain", items: ["Select a recipe row to open the designer and inspect versions, process structure, parameters, and review status.", "When authorized, create a recipe or import JSON. For a new recipe, enter its code, name, product code, and product name. Before importing, verify the exported package and product details; import creates a new draft v1 from a package version, skips existing recipe codes without overwriting them, and carries no approval records, so the recipe must be reviewed in this system.", "Use export only when permitted; verify the file contents and version before sharing it."] },
      { title: "Result boundaries", items: ["Duplicate-code feedback only covers data currently loaded in the browser; the server remains authoritative when saving.", "Verify the source and target before importing. Opening the import flow does not guarantee that the imported process is compatible."] }
    ]
  },
  recipeDesigner: {
    "zh-CN": [
      { title: "编辑工艺结构", items: ["先确认配方编码、产品、当前版本和版本状态；通过版本选择器查看其他版本，使用版本对比检查差异。", "维护单元规程、工步、泳道和连线。页面提示同一单元内串行、并行单元之间不互连；跨单元连接应从前一单元末工步指向下一单元首工步。", "调整工步顺序及参数槽时，核对名称、设定值、上下限、单位和语义；按工艺要求决定是否写入 PLC、归档为质检值、随批次缩放或填写实测点。"] },
      { title: "保存与审核", items: ["保存前查看页面校验结果和版本差异；保存工艺需要填写变更原因并完成电子签名。", "点击提交审核会先保存当前工艺，再要求提交签名；不必先单独保存，但看到未保存标记时不要离开，以免丢失修改。", "驳回版本可按页面提供的入口重开；存在已生效版本且没有草稿时，可按权限创建升版。审核中的版本可在审核台处理，设计器也会向当前节点所需角色提供通过/驳回操作。"] },
      { title: "关键约束", items: ["上位机工步不应配置 PLC 写参；非上位机工步按要求填写 PLC 程序号。实测值语义需要填写实测点。", "提交审核还会校验工艺非空、流程拓扑及 ISA-88 约束、PLC 时长可写范围，以及质量归档参数是否具有实测来源。", "删除工步会同时删除其参数槽及相关连线，且该操作不可撤销；页面校验不替代现场工艺和 PLC 执行验证。"] }
    ],
    en: [
      { title: "Edit the process structure", items: ["First verify the recipe code, product, selected version, and status. Use the version selector for other versions and compare versions to review differences.", "Maintain unit procedures, steps, lanes, and links. The page indicates serial flow within a unit, no links between parallel units, and cross-unit links from the preceding unit's last step to the next unit's first step.", "When changing steps or parameter slots, verify names, setpoints, limits, units, and semantics. Set PLC write, quality archival, batch scaling, and measured-point options according to the process requirements."] },
      { title: "Save and submit", items: ["Review validation results and version differences before saving; saving the procedure requires a change reason and e-signature.", "Submit for review first saves the current procedure and then requests a separate submission signature. A separate save is not required first; do not leave while the unsaved marker is visible.", "A rejected version can be reopened through the available action. When an approved version exists and no draft is present, an authorized user can create a new revision. In-review versions can be handled in the approvals workspace; the designer also offers approve/reject actions to the role required by the current step."] },
      { title: "Important constraints", items: ["Do not configure PLC writes for host-computer steps. Enter the PLC program number for other step types as required. Measured-value semantics require a measured point.", "Submission also validates a non-empty procedure, process topology and ISA-88 constraints, writable PLC duration ranges, and measurement sources for quality-archived parameters.", "Deleting a step also deletes its parameter slots and related links, and cannot be undone. Client-side validation does not replace site and PLC execution verification."] }
    ]
  },
  approvals: {
    "zh-CN": [
      { title: "定位待审版本", items: ["列表显示所有审核中的配方，并非只显示当前角色可签的项目；可按编码、名称、产品或待审节点搜索。只有待审节点要求的角色可提交决定。", "选择配方后，先核对送审版本、待审节点提示及其与生效版本的差异。待审版与生效版不同时，必须先成功加载差异才能通过；加载中或失败时“通过”和 Ctrl+Enter 会禁用，可重试加载。", "签署链展示已完成及待处理节点；提交人或已在该版本其他节点签过名的用户不能再次签署。审核内容以只读方式展示工艺流和参数矩阵。"] },
      { title: "提交审核决定", items: ["确认自己是当前节点要求的角色且未在该版本签署过，再选择通过或驳回。", "通过会将处理结果写入该审核节点，后续是否还有节点取决于审批链。审核台驳回弹窗要求填写原因；通过意见可选。", "差异加载门控仅限制通过，不限制驳回。完成后继续处理列表中的下一项；需要编辑配方内容时跳转设计器。"] },
      { title: "权限与边界", items: ["页面按当前待审节点显示可用操作；角色或审批规则以服务端最终校验为准。", "不要把节点名称理解成固定角色顺序；不同配方可能使用不同审批链。"] }
    ],
    en: [
      { title: "Locate a pending version", items: ["The list shows all recipes in review, not only items your role can sign. Search by code, name, product, or pending step. Only the role required by the pending step can decide.", "Verify the submitted version, pending-step prompt, and differences from the approved version. When versions differ, comparison must load successfully before approval; Approve and Ctrl+Enter are disabled while loading or after failure, with a retry available.", "The signature chain shows completed and pending steps. The submitter and anyone who has already signed another step for this version cannot sign again. Process flow and parameter matrix are read-only here."] },
      { title: "Submit a decision", items: ["Verify that your role matches the current review step and that you have not already signed this version.", "Approval records a decision for that step; whether another step follows depends on the chain. The approvals-page rejection dialog requires a reason; an approval comment is optional.", "The comparison gate applies to approval, not rejection. Continue with the next item after submitting; navigate to the designer if recipe content needs changes."] },
      { title: "Access and boundaries", items: ["Available actions are shown for the current pending step; the server performs the final authorization and validation.", "Step names are configurable and do not imply a fixed role sequence across all recipes."] }
    ]
  },
  approvalChains: {
    "zh-CN": [
      { title: "配置审核顺序", items: ["从列表选择现有审批链，或创建新链；核对链编码、名称、默认及启用状态。已有链的编码不可修改。", "按实际顺序添加审核节点，配置节点名称、要求角色及通过/驳回时的签署含义；待签提示由节点名称自动生成。使用上移、下移调整顺序。", "至少配置一个节点；节点名称和通过/驳回含义必填，同一角色不能重复。页面不允许将工艺工程师或操作员设为审核角色。"] },
      { title: "保存与维护", items: ["仅管理员可保存或删除审批链；保存前逐项检查节点次序、角色和签署含义，再按要求完成电子签名。", "删除审批链也需电子签名；仍被配方引用的链不能删除。默认链不能直接停用或删除，需先将默认标记移交给另一条链。", "停用仍被配方选用的链会导致后续提交失败；配方选择器可能列出停用链，请只选择启用链。"] },
      { title: "版本影响", items: ["修改审批链只影响之后提交的配方版本；已经进入审核的版本继续使用提交时记录的审批配置。", "若审批路径有变化，先与流程负责人确认生效时机，避免正在审核的版本与新版本规则混淆。"] }
    ],
    en: [
      { title: "Configure review order", items: ["Select an existing chain or create one. Verify its code, name, default flag, and enabled state. An existing chain's code cannot be changed.", "Add steps in order and set each step's title, required role, and signature meanings for approval/rejection. The pending-signature prompt is generated from the step title; use the move controls to reorder.", "Configure at least one step and provide its title and both meanings. A role cannot appear twice; process engineers and operators are not available as review roles."] },
      { title: "Save and maintain", items: ["Only administrators can save or delete chains. Check every step's order, role, and signature meanings before saving, then complete the e-signature requirement.", "Deleting a chain also requires an e-signature; a chain referenced by a recipe cannot be deleted. Transfer the default flag to another chain before disabling or deleting the default chain.", "Disabling a chain still selected by a recipe prevents future submissions. The recipe selector may list disabled chains, so select an enabled chain only."] },
      { title: "Version impact", items: ["Changes apply to recipe versions submitted in the future. Versions already in review retain the approval configuration captured at submission.", "Coordinate the effective time of workflow changes with the process owner to avoid confusion between in-review and new submissions."] }
    ]
  },
  batches: {
    "zh-CN": [
      { title: "查询与跟踪", items: ["按批次号、产品名、设备编码或主配方名称搜索，并使用状态筛选定位批次；当前实现不支持按配方编码或设备名称搜索。", "列表显示批次状态、快照配方名称及设备摘要；配方改名后，搜索匹配当前名称而列表可能仍显示快照名称。选择一行进入批次监控。", "注意“待检终样”等状态表示流程节点，不等同于运行中或已放行；可通过表头排序和分页浏览结果。"] },
      { title: "创建生产批次", items: ["选择已批准的主配方，核对批次号、主设备、缩放因子、产出批号和投料物料批；系统使用该配方当前已批准版本生成控制配方快照。", "多单元配方还需为单元绑定设备；设备不兼容时不可选。若设备正在占用，页面提示可创建快照但启动需等待设备空闲。", "同一设备被多个单元共用时会串行执行；提交前检查设备分配是否符合现场排程，创建后核对快照版本。"] },
      { title: "提交前检查", items: ["创建表单选择主配方而非版本；系统使用其当前已批准版本。创建后在批次快照中核对版本，并确认设备和物料批。", "批次号重复检查可能只覆盖当前已加载记录；以服务端创建结果为准。"] }
    ],
    en: [
      { title: "Search and track", items: ["Search by batch number, product name, equipment code, or master-recipe name, then filter by status. Recipe code and equipment name are not searchable.", "The list shows the recipe name from the batch snapshot, while search matches the recipe's current name; after a rename, these may differ. Select a row to open its monitor.", "A state such as Final sample pending is a workflow stage, not running or released. Use column sorting and pagination to browse results."] },
      { title: "Create a production batch", items: ["Select an approved master recipe and verify the batch number, primary equipment, scale factor, output lot, and input material lots. The system snapshots the recipe's currently approved version.", "For multi-unit recipes, assign equipment to each unit. Incompatible equipment cannot be selected. If equipment is occupied, the page indicates that a snapshot can be created but starting must wait until it is free.", "When multiple units share equipment, execution is serialized. Check the assignment against the production schedule and verify the snapshot version after creation."] },
      { title: "Before submitting", items: ["The create form selects a master recipe, not a version; the system uses its current approved version. Verify that version in the batch snapshot after creation, along with equipment and material lots.", "Duplicate-number checks may only cover records currently loaded; the server response is authoritative."] }
    ]
  },
  batchMonitor: {
    "zh-CN": [
      { title: "读取实时状态", items: ["先核对批次号、配方快照、设备和批次状态，确认没有打开错误批次。", "在运行、趋势、报警和履历页签间切换查看详情；多设备批次可选择车道，选择工步后质检数据会随之联动。", "查看设定参数矩阵、PLC 写参计划、趋势、报警和四步握手履历；趋势/履历可能截取部分数据，留意页面显示的总量提示。"] },
      { title: "执行控制", items: ["启动、保持、恢复和中止由操作员或主管执行；人工确认由操作员、主管或质量执行；跳步仅限主管。可用操作还受批次状态限制。", "运行中的保持是签名后的请求，调度器等待 PLC 确认 PLC_Held 后才暂停；排队批次的保持会直接生效。提交后检查状态和履历确认结果。", "跳步仅允许跳过未完成工步：运行中须处于安全握手相位；保持或故障批次按离线规则处理，已完成工步不可跳过。按权限取样，并核对样品关联工步与批次。"] },
      { title: "安全与验证", items: ["运行控制会影响实际批次；按钮可见只代表前端允许发起，不代表设备或服务端已成功执行。操作后检查状态和履历变化。", "报警列表可能只加载最近一段数据；要核对完整记录时查看电子批记录。"] }
    ],
    en: [
      { title: "Read live status", items: ["Verify the batch number, recipe snapshot, equipment, and batch state before reviewing or acting.", "Switch among Run, Trend, Alarms, and History. For multi-equipment batches, select a lane; selecting a step also updates the quality data shown.", "Review the setpoint matrix, PLC write plan, trends, alarms, and four-step handshake history. Trend and history data may be truncated; check any total-count indicator."] },
      { title: "Control execution", items: ["Operators or supervisors can start, hold, resume, or abort; operators, supervisors, or Quality can confirm a manual step; only supervisors can skip. Available actions also depend on batch state.", "A hold during execution is a signed request; the scheduler waits for PLC_Held before pausing. A queued batch is held directly. Check state and history after submitting.", "Skip applies only to unfinished steps. A running batch requires a safe handshake phase; held or faulted batches use the offline skip rule. Completed steps cannot be skipped. Take samples when authorized and verify the linked step and batch."] },
      { title: "Safety and verification", items: ["Run controls affect an active batch. A visible button only means the client can submit a request; verify the resulting state and history to confirm execution.", "The alarm list may contain only recent entries. Open the electronic batch record when the complete record is needed."] }
    ]
  },
  batchRecord: {
    "zh-CN": [
      { title: "核对归档内容", items: ["先确认批次抬头和归档配方快照，再依次检查工艺工步、参数结果、握手时序、报警、配方漂移和电子签名。", "查看物料谱系，确认投料批与产出追溯关系；查看实验室样品、判定结果及关联工步。", "可返回批次监控继续查看实时信息，或使用打印和 PDF/A 导出形成归档副本。"] },
      { title: "质量处置与签名", items: ["电子批记录及 PDF 仅主管、质量和管理员可查看；批次完成后页面仅向质量角色显示放行/拒收操作，拒收必须填写原因。", "放行前须处理完所有待判终检（Final）样品。归档质检结果超差、缺少实测值或存在不合格样品时，放行意见必须说明理由。", "放行前核查实验室样品判定签名状态；Mismatch 或 Unsupported 会阻止放行。放行和拒收均绑定当前批记录证据摘要；若返回 409 EVIDENCE_STALE，刷新并重新核对记录后再签。", "页面仅在批次完成后向质量角色显示样品判定；样品仍须为待判状态，不合格判定必须填写说明。处置后核对页面反馈。"] },
      { title: "追溯边界", items: ["此页面用于审阅归档记录，不应把它当成修改已归档工艺或实时参数的入口。", "放行条件和意见是否必填以服务端最终校验为准；需要解释偏差时应填写清晰说明。"] }
    ],
    en: [
      { title: "Review the archived record", items: ["Verify the batch header and archived recipe snapshot, then review process steps, parameter results, handshake timing, alarms, recipe drift, and signatures.", "Inspect material genealogy for input-to-output traceability. Review lab samples, dispositions, and their linked steps.", "Return to the batch monitor for live information, or use Print and PDF/A export to create an archive copy."] },
      { title: "Quality disposition and signatures", items: ["The electronic batch record and PDF are available only to supervisors, Quality, and administrators. After completion, only Quality users see release/reject actions; rejection requires a reason.", "Resolve all pending final samples before release. If archived quality results are out of specification, missing measured values, or include a failed sample, the release comment must explain the rationale.", "Check lab-sample disposition-signature integrity before release; Mismatch or Unsupported blocks release. Release and rejection bind the current batch-record evidence digest. If submission returns 409 EVIDENCE_STALE, refresh and review the record before signing again.", "The page shows sample-disposition actions to Quality users only after batch completion. A sample must still be pending, and a failed disposition requires an explanation. Verify the result after submitting."] },
      { title: "Traceability boundaries", items: ["This page is for reviewing the archived record, not for changing archived process content or live parameters.", "Release requirements and whether a comment is mandatory are ultimately validated by the server. Provide a clear explanation when documenting a deviation."] }
    ]
  },
  materialLots: {
    "zh-CN": [
      { title: "查询物料批", items: ["按批次号或物料关键字搜索；使用表头排序和分页定位记录。", "查看批次号、物料、来源、状态、数量及登记时间；选择一行打开物料谱系。", "列表无结果时先清空或缩短搜索条件，再检查是否需要切换分页。"] },
      { title: "登记来料批", items: ["选择登记入口，填写批次号、物料编码、物料名称、数量和单位。提交前核对单位与数量，确保与来料凭证一致。", "登记完成后回到列表确认记录，再进入谱系查看后续拆分或投料关系。", "该表单不提供检验状态字段；不要在此页面假定已完成质量检验或放行。"] },
      { title: "数据边界", items: ["批号重复提示可能只检查当前已加载数据，最终结果以服务端校验为准。", "状态与来源由页面展示数据提供；需要更改或处置时先确认相应业务入口和权限。"] }
    ],
    en: [
      { title: "Find material lots", items: ["Search by lot number or material keyword, then use column sorting and pagination to locate a record.", "Review lot number, material, source, status, quantity, and registration time. Select a row to open its genealogy.", "If no result appears, clear or shorten the search term and check whether another page is selected."] },
      { title: "Register an incoming lot", items: ["Open the registration form and enter lot number, material code, material name, quantity, and unit. Verify the quantity and unit against the incoming documentation.", "After registration, confirm the new row in the list, then open its genealogy to follow later splits or consumption.", "The form has no inspection-status field; do not assume this page records quality inspection or release."] },
      { title: "Data boundaries", items: ["Duplicate-lot feedback may only check currently loaded records; the server is authoritative.", "Status and source are displayed data. Confirm the correct workflow and access before attempting any disposition or change."] }
    ]
  },
  lotGenealogy: {
    "zh-CN": [
      { title: "沿谱系查看", items: ["页首显示当前物料批及其物料、来源、状态和数量。先确认当前节点，避免在错误批次上操作。", "查看祖先批了解来源，查看子批了解拆分；关联生产批次会显示投料角色、批次和数量。", "点击祖先或子批链接可切换谱系节点；浏览后可返回物料批列表。"] },
      { title: "拆分子批", items: ["当前批为 Open 时页面显示拆分入口；实际仅管理员、操作员、主管和质量角色可提交，入口可见不代表已获授权。填写子批批号和拆分数量，提交前核对父批、数量和单位。", "提交会创建子批，并从母批数量中扣减拆分数量；成功后页面跳转到新子批，可核对谱系关系。", "页面表单未展示父批剩余量、批号唯一性等完整校验；严格以服务端提交结果为准。"] },
      { title: "追溯用途", items: ["通过祖先链追原料来源，通过子批和生产批次查看拆分及投料去向。", "若关系或数量与业务记录不符，先核对相关物料批和生产批记录，再进行后续操作。"] }
    ],
    en: [
      { title: "Navigate the genealogy", items: ["The header identifies the current lot, material, source, state, and quantity. Verify the current node before taking action.", "Review ancestor lots for origin and child lots for splits. Linked production batches show consumption role, batch, and quantity.", "Select an ancestor or child link to move to that node; return to the material-lot list when finished."] },
      { title: "Split a child lot", items: ["The page shows the split action when the lot is Open, but only Admin, Operator, Supervisor, and Quality can submit it. Visibility does not guarantee authorization. Verify the parent lot, quantity, and unit.", "Submitting creates a child lot and subtracts the split quantity from the parent. After success, the page opens the child lot for verification.", "The form does not expose all checks, such as remaining parent quantity or lot-number uniqueness; rely on the server response."] },
      { title: "Traceability use", items: ["Follow ancestors to trace source material and children or production batches to review splits and consumption destinations.", "If a relationship or quantity differs from business records, verify the related material lots and batch record before proceeding."] }
    ]
  },
  alarms: {
    "zh-CN": [
      { title: "筛选与定位", items: ["页面默认显示未确认报警；可切换查看全部。按批次、报警代码或说明搜索，使用排序和分页查看结果。", "逐项检查时间、批次、工步、代码、级别、说明及确认人；选择报警行可跳到关联批次。", "先处理当前页高优先级或影响运行的报警，再查看其他分页；批量确认只作用于当前页。"] },
      { title: "确认报警", items: ["单条确认前先核对报警详情及关联批次，确认现场或工艺问题已按流程处置。", "确认本页会批量确认本页未确认项，操作前检查当前筛选和页码，并在确认提示中再次核对。", "确认只更新确认状态；报警记录仍保留供审计和追溯。"] },
      { title: "权限与注意事项", items: ["管理员、操作员、主管和质量可查看报警；确认操作仅操作员、主管和质量角色可执行。", "报警确认不等于故障自动消除；需要回到批次监控或设备页检查问题是否真正恢复。", "分页和批量操作范围以当前显示页面为准；若请求部分失败，刷新并核对各条记录状态。"] }
    ],
    en: [
      { title: "Filter and locate", items: ["The page defaults to unacknowledged alarms; switch to All to include acknowledged entries. Search by batch, alarm code, or description, then sort and paginate.", "Review time, batch, step, code, severity, description, and acknowledging user. Select a row to open its associated batch.", "Address high-priority or run-impacting alarms on the current page first; bulk acknowledgement applies only to that page."] },
      { title: "Acknowledge alarms", items: ["Before acknowledging an individual alarm, verify its details and batch and ensure the issue has been handled according to procedure.", "Acknowledge current page affects unacknowledged entries on the displayed page. Check the filters and page number, then verify the confirmation prompt.", "Acknowledgement changes the acknowledgement state; the alarm record remains available for audit and traceability."] },
      { title: "Access and important notes", items: ["Admins, Operators, Supervisors, and Quality users can view alarms. Only Operators, Supervisors, and Quality users can acknowledge them.", "Acknowledgement does not mean the fault has cleared. Return to the batch monitor or equipment page to verify recovery.", "Pagination and bulk-action scope follow the current page. If a request partially fails, refresh and check each record's state."] }
    ]
  },
  equipment: {
    "zh-CN": [
      { title: "设备列表与定位", items: [
        "页面有“设备”和“相库”两个页签。设备页搜索覆盖编码、名称、主机、PLC 型号、设备类、协议和占用批次号；设备类未设置时显示“未分类”。",
        "列表中的启用状态表示设备配置是否启用；占用列表示当前占用状态，不等同于设备连接是否正常。点击可跳转的批次号可打开占用批次。",
        "管理员可新增/编辑设备及校验点表；有相应角色时可测试连接或使用仿真故障入口。只读用户看到的是查看操作。设备编码创建后不可修改，设备名称为必填项。"
      ] },
      { title: "设备表单与握手点表", items: [
        "基本信息包括协议（Simulator、Siemens S7、Modbus TCP、OPC UA）、主机/端口、PLC 型号、机架/插槽、启用状态和设备类。设备类用于相能力匹配；留空会跳过相能力校验，不适用于需要按设备类约束的配置。",
        "握手看门狗分别配置等待 Ready、写参、应答、心跳、复位、保持应答和空闲安定超时。握手点表映射 Step_ID、Step_Type、Trigger_Write、PLC_Ready、Step_Running、Step_Complete、Step_Error、Host_Hold、PLC_Held、Error_Code 和 Heartbeat 等现场地址。",
        "写参槽把配方参数槽映射到 PLC 地址；默认地址从 DB10.20 起按每槽 4 字节递增，必须按现场 PLC 点表核对并在不一致时修改。实测点以键名映射地址，配方中声明的 measuredTag 必须与设备实测点键名一致，否则开批校验会拦截。",
        "OPC UA 可配置安全端点、证书接受策略和用户名/密码；用户名留空表示匿名连接。按 PLC 服务端安全策略配置，不要因连接测试成功就推断握手与地址映射正确。"
      ] },
      { title: "相库与配方引用", items: [
        "相库按设备类折叠展示；展开后可查看设备类编码/名称、模板数量，以及每条模板的编码、名称、执行类、程序号、工艺操作、看门狗和参数槽数。搜索可匹配设备类信息及模板编码、名称、操作和程序号。",
        "相模板包含 PLC 执行相的类型别名、PLC 程序号、工艺操作、看门狗及参数默认值。程序号使用 1–6 或 9–99；0、7、8 保留给上位机工步。模板编码全局唯一，同一设备类中的程序号不能重复；名称留空时使用类型名。",
        "参数槽可维护名称、设定值、上下限、单位、是否写 PLC、是否归档为质检值、是否随批缩放、参数语义和实测点。把模板添加到配方后，数据进入配方工步；后续改/删模板不会自动同步已保存工步。",
        "相库只维护 PLC 写参相模板；等待、质检和人工确认由上位机处理，不可作为相模板创建。删除模板前查看确认提示，并确认后续新编排不再需要它。"
      ] },
      { title: "权限与验证边界", items: [
        "相库新增、编辑和删除权限限管理员或工艺工程师；设备编辑、点表校验等按钮按角色显示，服务端仍会再次校验权限。",
        "“测试连接”用于检查连接尝试及响应，不等于已经验证握手时序、每个点位地址或配方可执行性；“校验点表”也不能替代现场受控试运行。",
        "仿真故障只适用于 Simulator 设备，且部署必须启用仿真运行时；它用于受控验证握手故障处理，不会也不能操作真实 PLC。变更连接参数或点表前确认目标设备、维护状态、现场版本和授权；尤其不可绕过 PLC_Ready 盲写。"
      ] }
    ],
    en: [
      { title: "Equipment list and search", items: [
        "The page has Equipment and Phase library tabs. Equipment search covers code, name, host, PLC model, equipment class, protocol, and occupying batch number; unassigned devices show Unclassified.",
        "Enabled describes whether the equipment configuration is enabled. Occupancy describes current use and is not a connection-health indicator. Select a linked batch number to open the occupying batch.",
        "Administrators can add/edit equipment and validate point maps; connection tests and simulation-fault actions are shown according to role. Read-only users see View. Equipment code is immutable after creation, and equipment name is required."
      ] },
      { title: "Equipment form and handshake map", items: [
        "Basic settings include protocol (Simulator, Siemens S7, Modbus TCP, OPC UA), host/port, PLC model, rack/slot, enabled state, and equipment class. The class is used for phase-capability matching; leaving it blank skips that check and is not suitable when class-based constraints are required.",
        "Handshake watchdogs cover Ready wait, parameter-write, acknowledgement, heartbeat, reset, hold acknowledgement, and idle-settle timeouts. The point map assigns field addresses for Step_ID, Step_Type, Trigger_Write, PLC_Ready, Step_Running, Step_Complete, Step_Error, Host_Hold, PLC_Held, Error_Code, and Heartbeat.",
        "Write slots map recipe parameter slots to PLC addresses. Defaults start at DB10.20 and advance by four bytes per slot; compare them with the approved plant point map and change them when it differs. Measured points map keys to addresses; a recipe measuredTag must match a configured key or batch-start validation will block it.",
        "OPC UA settings include secure endpoint, certificate-acceptance policy, and username/password; a blank username means anonymous access. Follow the PLC server's security policy. A successful connection test does not prove the handshake or address mapping."
      ] },
      { title: "Phase library and recipe use", items: [
        "The phase library groups entries by equipment class. Expanded rows show class code/name, template count, and each template's code, name, execution kind, program number, operation, watchdog, and parameter-slot count. Search matches class details plus template code, name, operation, and program number.",
        "A phase template defines a PLC-executed phase alias, PLC program number, process operation, watchdog, and default parameter values. Program numbers are 1–6 or 9–99; 0, 7, and 8 are reserved for host-side steps. Template codes are globally unique, program numbers cannot repeat within a class, and a blank name falls back to the type name.",
        "Parameter slots include name, setpoint, limits, unit, PLC-write flag, quality-archival flag, batch-scaling flag, semantic, and measured point. Adding a template puts its values into a recipe step; later template edits or deletion do not automatically update saved steps.",
        "The library is only for PLC-writing phase templates. Wait, quality, and manual-confirm steps are handled by the host and cannot be created as library templates. Review the delete confirmation and ensure future designs no longer need the template."
      ] },
      { title: "Access and verification limits", items: [
        "Only administrators or process engineers can add, edit, or delete phase templates. Equipment editing and point-map actions are role-gated in the UI, and the server checks authorization again.",
        "Test connection checks a connection attempt and response; it does not prove handshake timing, every point address, or recipe executability. Point-map validation is not a substitute for a controlled plant run.",
        "Simulation-fault actions are only for Simulator equipment and require the simulator runtime to be enabled in the deployment. They support controlled handshake-fault verification and cannot operate a real PLC. Before changing connection settings or point maps, verify the target, maintenance state, approved plant configuration, and authorization. Never bypass PLC_Ready for blind writes."
      ] }
    ]
  },
  audit: {
    "zh-CN": [
      { title: "搜索审计记录", items: ["输入用户、动作或详情关键字；按实体类型筛选主配方、批次、设备、相模板、用户、物料批、实验室样品或报警。", "表格默认按时间倒序分页；检查操作者、动作、实体、时间和详情，选择可跳转记录查看关联对象。", "实验室样品审计记录在详情中解析到批次后才能跳转；无法解析时可使用批次号或详情关键字搜索。"] },
      { title: "还原操作背景", items: ["用实体类型和关键字缩小范围，再沿关联对象进入配方、批次或设备页面核对当前业务状态。", "结合相邻时间的审计记录还原操作顺序；不要仅凭单条摘要推断完整处置过程。"] },
      { title: "权限、只读与筛选范围", items: ["本页仅质量和管理员可访问；审计记录用于追溯，不提供修改或删除操作。", "页面没有时间范围控件；如需限定区间，应结合可用搜索条件或其他审计工具，不要假定列表已按自定义时间过滤。"] }
    ],
    en: [
      { title: "Search audit entries", items: ["Search by user, action, or detail keyword. Filter by entity type: master recipe, batch, equipment, phase template, user, material lot, lab sample, or alarm.", "The table is paginated and defaults to descending time order. Review actor, action, entity, time, and details; select a linked entry to open its related object.", "A lab-sample entry can navigate only when its details resolve to a batch. Otherwise, search by batch number or detail text."] },
      { title: "Reconstruct context", items: ["Narrow results by entity and keyword, then follow links to the recipe, batch, or equipment page to verify current business state.", "Use nearby audit entries to reconstruct sequence; a single summary may not show the complete disposition."] },
      { title: "Access, read-only, and filter scope", items: ["This page is restricted to Quality and Admin users. Audit entries support traceability; there is no edit or delete action.", "There is no time-range control on this page. Use available search conditions or another audit tool when a specific interval is required; do not assume a custom time filter is active."] }
    ]
  },
  users: {
    "zh-CN": [
      { title: "账号与角色", items: ["按登录名或显示名搜索用户；列表显示登录名、显示名、角色和启用状态。", "新建账号时填写登录名、显示名、密码并分配一个岗位角色；登录名至少 3 位，密码至少 8 位。", "编辑时登录名不可更改；密码留空表示不修改。不能停用当前登录账号或最后一个启用的管理员；其他账号按最小权限原则管理。"] },
      { title: "备份与维护", items: ["备份区显示自动备份状态、UTC 计划时刻、保留份数、下次执行时间、目录和文件清单；下次执行和文件时间按浏览器本地时区显示。", "可手动创建服务器快照、执行数据库维护或下载整库。每次整库下载都要求管理员电子签名，下载文件包含敏感数据，需按安全规则保管。", "列表仅显示日常快照，不含 pre-migration 升级前快照。快照通过完整性检查后，保留份数大于 0 时才会裁剪超额旧快照；为 0 或负数时不裁剪。"] },
      { title: "维护与恢复边界", items: ["手动维护先运行 PRAGMA optimize；只有数据库空闲且符合配置阈值时才运行 VACUUM。手动维护不会先自动创建备份。", "页面没有备份恢复/恢复演练入口；生成快照时虽会执行完整性检查，但文件存在或检查通过不代表已验证可恢复。维护前确认近期快照，并通过独立恢复演练验证。"] }
    ],
    en: [
      { title: "Accounts and roles", items: ["Search by login name or display name. The list shows login name, display name, role, and enabled state.", "When creating an account, enter a login name, display name, password, and one job-required role. Login names require at least three characters and passwords at least eight.", "An existing login name cannot be changed. Leave the password blank when editing to keep it unchanged. The current account and the last enabled administrator cannot be disabled."] },
      { title: "Backups and maintenance", items: ["The backup area shows automatic-backup state, UTC schedule time, retention count, next run, directory, and file list. Next-run and file timestamps use the browser's local time zone.", "You can create a server snapshot, run database maintenance, or download the full database. Every full-database download requires an administrator e-signature; protect the sensitive downloaded file.", "The list shows daily snapshots, not pre-migration snapshots. After integrity checks pass, excess snapshots are pruned only when retention is positive; zero or a negative value disables pruning."] },
      { title: "Maintenance and recovery limits", items: ["Manual maintenance runs PRAGMA optimize first; VACUUM runs only when the database is idle and configured thresholds are met. Manual maintenance does not create a backup first.", "There is no restore or recovery-test action here. Snapshot creation performs an integrity check, but a listed or checked file is not proof of recoverability. Confirm a recent snapshot and verify recovery through a separate restore test."] }
    ]
  },
  login: {
    "zh-CN": [
      { title: "登录步骤", items: ["输入已分配的用户名和密码，再提交登录；用户名或密码为空时页面会阻止提交。", "登录成功后返回可接受的站内路径；没有或不接受返回地址时进入运行总览。已知路径的权限检查在登录后执行。", "仅开发环境显示演示账号快捷填入；生产环境不会显示该入口。不要将演示凭证用于生产系统。"] },
      { title: "登录失败时", items: ["重新确认用户名拼写、键盘输入状态和密码；认证失败提示不会区分账号不存在、账号停用或密码错误。", "同一用户名 10 分钟内失败达到 8 次会暂时锁定。若登录成功但无法进入某功能，请联系管理员核对角色权限。"] },
      { title: "账号安全", items: ["不要共享密码；离开共享设备前退出账号。", "通过可信系统地址登录，收到异常登录提示时按组织安全流程处理。"] }
    ],
    en: [
      { title: "Sign-in steps", items: ["Enter your assigned username and password, then submit. The page blocks submission if either field is empty.", "After successful sign-in, the app returns to an accepted in-app path; otherwise it opens the dashboard. Role checks for known routes run after sign-in.", "Demo-account shortcuts appear only in development, not production. Never use demo credentials in a production system."] },
      { title: "If sign-in fails", items: ["Recheck the username spelling, keyboard input state, and password. The authentication error does not distinguish an unknown account, a disabled account, or an incorrect password.", "Eight failures for the same username within ten minutes trigger a temporary lockout. If sign-in succeeds but a feature is blocked, ask an administrator to check role permissions."] },
      { title: "Account security", items: ["Do not share your password; sign out before leaving a shared device.", "Use a trusted system address and follow organizational security procedures if you receive an unexpected sign-in alert."] }
    ]
  },
  notFound: {
    "zh-CN": [
      { title: "确认地址", items: ["页面会显示当前访问地址；检查路径拼写、批次或资源 ID 是否正确。", "书签或分享链接可能已过期，或目标路径已变更。本页面表示前端路由未匹配；角色权限不足会由系统提示并返回运行总览。"] },
      { title: "继续导航", items: ["选择“返回运行总览”进入可用入口；也可使用“返回上一页”回到浏览历史中的上一个页面。", "若没有浏览器历史记录，返回上一页会改为回到运行总览。之后可通过导航菜单重新进入目标功能。"] }
    ],
    en: [
      { title: "Check the address", items: ["The page displays the current address. Verify the path spelling and any batch or resource ID.", "A bookmark or shared link may be outdated, or the route may have changed. This page means no frontend route matched; role restrictions display a warning and return to the dashboard."] },
      { title: "Continue navigating", items: ["Choose Go to dashboard to open an available entry point, or use Back to return to the previous page in browser history.", "If no browser history is available, Back returns to the dashboard. Use the navigation menu to locate the intended feature."] }
    ]
  }
} as const;

type PageGuideKey = keyof typeof PAGE_GUIDES;
const props = defineProps<{ guideKey: PageGuideKey }>();
const { locale } = useI18n({ useScope: "global" });
const router = useRouter();
const open = ref(false);
const isEnglish = computed(() => locale.value === "en");
const guide = computed<GuideCopy>(() => PAGE_GUIDES[props.guideKey][isEnglish.value ? "en" : "zh-CN"]);
const details = computed<readonly GuideSection[]>(
  () => PAGE_GUIDE_DETAILS[props.guideKey][isEnglish.value ? "en" : "zh-CN"]
);
const fullArticlePath = computed(() => {
  const articleId = PAGE_GUIDE_ARTICLE_BY_KEY[props.guideKey];
  return articleId ? router.resolve(`/help/${articleId}`).fullPath : "";
});
const labels = computed(() => isEnglish.value
  ? { button: "Page guide", steps: "Quick start", note: "Important note", fullGuide: "Read the full guide" }
  : { button: "本页说明", steps: "快速操作", note: "重要提醒", fullGuide: "查看完整说明" });
</script>

<style scoped>
.page-guide-trigger {
  display: inline-grid;
  place-items: center;
  width: 28px;
  height: 28px;
  margin-left: var(--space-1);
  padding: 0;
  border: 1px solid var(--line);
  border-radius: 50%;
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 14px;
  line-height: 1;
  vertical-align: middle;
  cursor: pointer;
}
.page-guide-trigger:hover {
  border-color: var(--accent);
  background: var(--hover);
  color: var(--accent-bright);
}
.page-guide-trigger:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}
.page-guide-content { color: var(--text-body); }
.page-guide-content p { margin-top: 0; line-height: 1.65; }
.page-guide-section {
  margin: var(--space-4) 0 var(--space-2);
  color: var(--accent-bright);
  font-size: 13px;
  font-weight: 600;
}
.page-guide-steps {
  margin: 0;
  padding: 0;
  list-style: none;
  counter-reset: page-guide-step;
}
.page-guide-steps li {
  display: grid;
  grid-template-columns: 28px minmax(0, 1fr);
  gap: var(--space-2);
  padding: var(--space-2) 0;
  border-top: 1px solid var(--line);
  line-height: 1.6;
}
.page-guide-step-index {
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}
.page-guide-detail {
  padding-top: 1px;
}
.page-guide-list {
  margin: 0;
  padding-left: 20px;
}
.page-guide-list li {
  padding: 4px 0;
  line-height: 1.65;
}
.page-guide-note {
  margin-top: var(--space-3);
  padding-top: var(--space-3);
  border-top: 1px solid var(--line);
  color: var(--muted);
}
.page-guide-full-link {
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  margin-top: var(--space-3);
  color: var(--accent-bright);
  font-size: 13px;
  font-weight: 600;
  text-decoration: none;
}
.page-guide-full-link:hover { text-decoration: underline; }
.page-guide-full-link:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 3px;
  border-radius: 2px;
}
</style>
