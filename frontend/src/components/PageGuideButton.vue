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
    </div>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref } from "vue";
import { useI18n } from "vue-i18n";

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
      steps: ["先处理故障、报警和待放行项；点击计数可直达列表。", "检查设备占用行，进入关联批次。", "在在途批次查看工步、时长与握手阶段。"]
    },
    en: {
      title: "Dashboard guide",
      purpose: "Review batches needing attention, open alarms, equipment occupancy, and active runs.",
      steps: ["Start with faults, alarms, and pending releases; select a count to open its list.", "Select an occupied equipment row to open its batch.", "Review the current step, elapsed time, and handshake phase in active batches."]
    }
  },
  recipes: {
    "zh-CN": {
      title: "主配方说明",
      purpose: "查询、筛选和维护主配方及其单元规程。",
      steps: ["按编码、名称或产品搜索配方。", "使用状态筛选定位草稿、审核中或已生效配方。", "打开配方进入设计；新建或导入前确认目标产品和版本。"]
    },
    en: {
      title: "Master recipes guide",
      purpose: "Find, filter, and maintain master recipes and their unit procedures.",
      steps: ["Search by code, name, or product.", "Filter drafts, items in review, or approved recipes.", "Open a recipe to design it; confirm the product and version before creating or importing."]
    }
  },
  recipeDesigner: {
    "zh-CN": {
      title: "配方设计说明",
      purpose: "维护配方的工艺结构、工步参数和审核提交内容。",
      steps: ["编辑单元规程、工步顺序及参数。", "保存前检查校验结果和版本差异。", "保存草稿后再提交审核；未保存标记消失前不要离开。"],
      note: "提交后的版本进入审核流程，已提交内容不应通过编辑草稿替代。"
    },
    en: {
      title: "Recipe designer guide",
      purpose: "Maintain recipe structure, step parameters, and review submissions.",
      steps: ["Edit unit procedures, step order, and parameters.", "Check validation results and version differences before saving.", "Save the draft before submitting; do not leave while the unsaved marker remains."],
      note: "Submitted versions enter review; do not replace a submitted version by editing a draft."
    }
  },
  approvals: {
    "zh-CN": {
      title: "多级审核说明",
      purpose: "按配方配置的审批链处理当前轮到本角色的审核任务。",
      steps: ["打开待审核配方，核对版本和差异。", "确认当前节点的审核意见与电子签名要求。", "提交通过或驳回决定后，继续处理下一项。"],
      note: "只能处理审批链中当前分配给自己的节点。"
    },
    en: {
      title: "Recipe approvals guide",
      purpose: "Process review tasks assigned to your role in each recipe's approval chain.",
      steps: ["Open a pending recipe and verify its version and differences.", "Check the current review step and e-signature requirements.", "Submit an approval or rejection, then continue with the next item."],
      note: "You can act only on the approval step currently assigned to you."
    }
  },
  approvalChains: {
    "zh-CN": {
      title: "审批链配置说明",
      purpose: "配置配方提交后依次由哪些角色审核，以及各节点展示的提示。",
      steps: ["选择已有审批链，或创建一条新链。", "按顺序配置审核节点、角色和节点提示。", "保存后检查配方使用的审批链是否正确。"],
      note: "修改审批链只影响之后提交的版本；在审版本的节点保持提交时的配置。"
    },
    en: {
      title: "Approval chain setup guide",
      purpose: "Configure which roles review submitted recipes and the prompts shown at each step.",
      steps: ["Select an existing chain or create a new one.", "Set review steps, roles, and prompts in order.", "After saving, verify the chain selected by each recipe."],
      note: "Changes apply to future submissions; in-review versions keep the chain captured at submission."
    }
  },
  batches: {
    "zh-CN": {
      title: "生产批次说明",
      purpose: "查询生产批次，并跟踪从配方创建到执行、放行的进度。",
      steps: ["按批次号、配方或设备筛选记录。", "查看批次状态和执行信息，打开记录进入实时监控。", "创建批次时选择已批准的主配方并核对设备。"]
    },
    en: {
      title: "Production batches guide",
      purpose: "Find production batches and follow progress from recipe selection through execution and release.",
      steps: ["Filter by batch number, recipe, or equipment.", "Review batch status and open a record for live monitoring.", "When creating a batch, select an approved recipe and verify the equipment."]
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
      steps: ["按批号或物料搜索来料批。", "登记新来料时核对物料编码、批号及检验状态。", "打开批次查看子批拆分和投料去向。"]
    },
    en: {
      title: "Material lots guide",
      purpose: "Register incoming material lots and trace their movement through production.",
      steps: ["Search incoming lots by lot number or material.", "When registering a lot, verify its material code, lot number, and inspection status.", "Open a lot to review splits and consumption destinations."]
    }
  },
  lotGenealogy: {
    "zh-CN": {
      title: "物料谱系说明",
      purpose: "从当前物料批向上追溯来源，向下查看拆分和投料去向。",
      steps: ["检查当前批次的物料、状态和来源。", "查看祖先批与子批，沿链接切换谱系节点。", "核对生产批次及投料关系后返回物料批列表。"]
    },
    en: {
      title: "Material genealogy guide",
      purpose: "Trace a material lot upstream to its source and downstream to splits and consumption.",
      steps: ["Verify the current lot's material, status, and source.", "Follow ancestor and child lots through the genealogy links.", "Confirm production batch and consumption relationships, then return to the lot list."]
    }
  },
  alarms: {
    "zh-CN": {
      title: "过程报警说明",
      purpose: "查看握手故障、质检超差和调度异常，并跟踪确认状态。",
      steps: ["优先筛选未确认报警并查看关联批次。", "根据报警说明核对设备、工步或质检数据。", "确认已处置的报警；确认记录仍保留供追溯。"]
    },
    en: {
      title: "Process alarms guide",
      purpose: "Review handshake faults, out-of-range quality results, and scheduling exceptions.",
      steps: ["Filter open alarms first and inspect the associated batch.", "Use the alarm details to verify equipment, step, or quality data.", "Acknowledge resolved alarms; acknowledged records remain available for traceability."]
    }
  },
  equipment: {
    "zh-CN": {
      title: "设备与 PLC 驱动说明",
      purpose: "维护产线设备、连接参数、设备类和相模板。",
      steps: ["选择设备或相模板区域，再按编码、名称或主机搜索。", "核对协议、连接参数及设备类后再保存变更。", "检查设备连接和可用相模板是否符合运行需要。"],
      note: "连接参数和模板会影响实际执行；修改前确认目标设备并遵循授权流程。"
    },
    en: {
      title: "Equipment and PLC guide",
      purpose: "Maintain line equipment, connection parameters, equipment classes, and phase templates.",
      steps: ["Choose the equipment or template area, then search by code, name, or host.", "Verify protocol, connection settings, and equipment class before saving changes.", "Check that the connection and phase templates meet run requirements."],
      note: "Connection settings and templates affect execution; verify the target equipment and your authorization."
    }
  },
  audit: {
    "zh-CN": {
      title: "操作审计说明",
      purpose: "按用户、动作、实体和时间范围查询关键操作留痕。",
      steps: ["设置时间范围，并按用户或实体类型筛选。", "搜索动作或详情，打开记录核对关联对象。", "结合批次、配方或设备页面还原操作闭环。"],
      note: "审计记录用于追溯，不可在此页面修改。"
    },
    en: {
      title: "Audit log guide",
      purpose: "Find significant actions by user, action, entity, and time range.",
      steps: ["Set a time range and filter by user or entity type.", "Search actions or details, then open a record to verify its related object.", "Use batch, recipe, or equipment pages to reconstruct the operation context."],
      note: "Audit entries are for traceability and cannot be edited here."
    }
  },
  users: {
    "zh-CN": {
      title: "用户与备份说明",
      purpose: "维护用户账号、角色权限，并查看数据库备份与维护状态。",
      steps: ["创建或编辑账号时核对登录名与角色。", "按岗位授予最小必要权限，离岗账号及时停用。", "检查备份状态；执行数据库维护前确认备份可用。"],
      note: "此页面仅管理员可访问；数据库文件应按安全要求保管。"
    },
    en: {
      title: "Users and backups guide",
      purpose: "Manage user accounts and roles, and review database backup and maintenance status.",
      steps: ["Verify the login name and role when creating or editing an account.", "Grant only the permissions needed for the job and disable departed users.", "Check backup status and confirm a usable backup before database maintenance."],
      note: "This page is restricted to administrators; protect database files according to security policy."
    }
  },
  login: {
    "zh-CN": {
      title: "登录说明",
      purpose: "使用已分配的账号进入工艺配方管理与实时执行系统。",
      steps: ["输入用户名和密码后选择登录。", "登录成功后返回原先访问的页面；没有来源地址时进入运行总览。", "如无法登录，请联系管理员核对账号状态。"],
      note: "不要与他人共享密码。"
    },
    en: {
      title: "Sign-in guide",
      purpose: "Use your assigned account to access the recipe and execution system.",
      steps: ["Enter your username and password, then select Sign in.", "After sign-in, you return to the requested page or the dashboard.", "If sign-in fails, ask an administrator to verify your account status."],
      note: "Do not share your password."
    }
  },
  notFound: {
    "zh-CN": {
      title: "页面不存在说明",
      purpose: "当前地址没有匹配到可访问的页面。",
      steps: ["检查地址是否拼写正确或是否为过期链接。", "使用侧边栏进入目标功能。", "需要时返回运行总览后重新导航。"]
    },
    en: {
      title: "Page not found guide",
      purpose: "The current address does not match an available page.",
      steps: ["Check the address for a typo or an outdated link.", "Use the sidebar to open the intended feature.", "If needed, return to the dashboard and navigate again."]
    }
  }
} as const;

const PAGE_GUIDE_DETAILS = {
  dashboard: {
    "zh-CN": [
      { title: "查看顺序", items: ["先看“需处理”卡片和最新未确认报警，点击可跳到对应批次或报警列表；卡片是否可跳转取决于当前数据和权限。", "再看设备占用表：占用状态、批次状态和握手阶段分别描述设备当前占用、关联批次进度及设备交互阶段；点击有批次的行进入批次详情。", "最后看在途批次表，结合批次状态、工步、时长、握手阶段和设备识别卡住或等待中的任务；点击行可进入监控。"] },
      { title: "数字与数据状态", items: ["上方“需处理”是优先处置入口；“参考”数字用于态势观察，不应与待办项混为一谈。", "执行态势仅显示最近 2 小时的批次事件；点击事件可进入关联批次。没有关联批次的事件不可跳转。", "数字显示“—”表示数据未知或尚未成功取得，不代表数量为零。取数失败时，若有上次成功数据，页面会明确提示其已过期；正常情况下页面会自动刷新。"] },
      { title: "筛选与跳转", items: ["设备数量较多时可启用“只看非空闲”，快速收敛到正在运行、排队、保持或故障相关设备。", "点击 KPI、报警条、设备占用行、在途批次行或关联事件，跳到相应处置页面；返回总览继续核对整体状态。"] }
    ],
    en: [
      { title: "Review sequence", items: ["Start with the actionable cards and latest unacknowledged alarm. Selecting a card opens its related batch or alarm list when the data and your access allow it.", "Next, review equipment occupancy: occupancy, batch status, and handshake phase describe equipment use, batch progress, and the interaction phase respectively. Select a row with a batch to open it.", "Then inspect active batches. Read status, step, elapsed time, handshake phase, and equipment together to identify work that may be waiting or stalled; select a row to open its monitor."] },
      { title: "Counts and data state", items: ["Actionable cards are the priority queue; the reference strip is for situational awareness and should not be confused with pending work.", "Execution events cover the last two hours. Select an event to open its batch; events without a batch cannot be opened this way.", "A dash means the data is unknown or has not loaded, not zero. If retrieval fails after a successful load, the page marks the retained values as stale; normal refresh is automatic."] },
      { title: "Filters and navigation", items: ["When many equipment rows are present, enable Only busy to focus on equipment that is running, queued, held, or faulted.", "Select a KPI, alarm strip, occupied-equipment row, active-batch row, or linked event to open its relevant workflow; return here to recheck overall status."] }
    ]
  },
  recipes: {
    "zh-CN": [
      { title: "查找与筛选", items: ["在搜索框输入配方编码、名称或产品关键字；使用状态筛选缩小结果。", "“草稿、审核中、驳回”根据草稿状态筛选；“已生效”表示存在已批准版本。生效版本与当前草稿/待审版本可能不同。", "表格中的“生效版本”“草稿状态”“待审节点”是不同信息；不要仅凭其中一列判断另一个版本的审核结果。"] },
      { title: "查看与维护", items: ["选择配方行进入设计器，查看版本、工艺结构、参数及审批状态。", "有权限时可新建或导入 JSON；新建时填写编码、名称、产品编码和产品名称，并在保存前核对目标产品。", "按权限使用导出；分享导出文件前确认其内容和版本适合接收者。"] },
      { title: "结果边界", items: ["编码重复提示取决于当前已加载数据，最终唯一性以保存时服务端校验为准。", "导入前确认文件来源和目标配方；页面入口本身不能保证导入内容与当前工艺兼容。"] }
    ],
    en: [
      { title: "Find and filter", items: ["Search by recipe code, name, or product keyword, then use the status filter to narrow the list.", "Draft, In review, and Rejected filter by draft status; Approved means an approved version exists. The approved version may differ from the current draft or submitted version.", "Approved version, draft status, and pending review step are separate facts. Do not infer one from another."] },
      { title: "Review and maintain", items: ["Select a recipe row to open the designer and inspect versions, process structure, parameters, and review status.", "When authorized, create a recipe or import JSON. For a new recipe, enter its code, name, product code, and product name, then verify the target product before saving.", "Use export only when permitted; verify the file contents and version before sharing it."] },
      { title: "Result boundaries", items: ["Duplicate-code feedback only covers data currently loaded in the browser; the server remains authoritative when saving.", "Verify the source and target before importing. Opening the import flow does not guarantee that the imported process is compatible."] }
    ]
  },
  recipeDesigner: {
    "zh-CN": [
      { title: "编辑工艺结构", items: ["先确认配方编码、产品、当前版本和版本状态；通过版本选择器查看其他版本，使用版本对比检查差异。", "维护单元规程、工步、泳道和连线。页面提示同一单元内串行、并行单元之间不互连；跨单元连接应从前一单元末工步指向下一单元首工步。", "调整工步顺序及参数槽时，核对名称、设定值、上下限、单位和语义；按工艺要求决定是否写入 PLC、归档为质检值、随批次缩放或填写实测点。"] },
      { title: "保存与审核", items: ["保存前查看页面校验结果和版本差异；需要电子签名的操作按弹窗要求完成。", "先保存草稿，再提交审核；看到未保存标记时不要离开，以免丢失修改。", "驳回版本可按页面提供的入口重开；存在已生效版本且没有草稿时，可按权限创建升版。审核中的版本应在审核台处理。"] },
      { title: "关键约束", items: ["上位机工步不应配置 PLC 写参；非上位机工步按要求填写 PLC 程序号。实测值语义需要填写实测点。", "删除工步会同时删除其参数槽及相关连线，且该操作不可撤销；删除前确认影响范围。", "页面校验只覆盖前端可见规则，实际工艺有效性及 PLC 执行行为仍需按现场和服务端验证结果确认。"] }
    ],
    en: [
      { title: "Edit the process structure", items: ["First verify the recipe code, product, selected version, and status. Use the version selector for other versions and compare versions to review differences.", "Maintain unit procedures, steps, lanes, and links. The page indicates serial flow within a unit, no links between parallel units, and cross-unit links from the preceding unit's last step to the next unit's first step.", "When changing steps or parameter slots, verify names, setpoints, limits, units, and semantics. Set PLC write, quality archival, batch scaling, and measured-point options according to the process requirements."] },
      { title: "Save and submit", items: ["Review validation results and version differences before saving; complete the e-signature prompt when required.", "Save the draft before submitting it for review. Do not leave while the unsaved marker is visible.", "A rejected version can be reopened through the available action. When an approved version exists and no draft is present, an authorized user can create a new revision. In-review versions are handled in the approvals page."] },
      { title: "Important constraints", items: ["Do not configure PLC writes for host-computer steps. Enter the PLC program number for other step types as required. Measured-value semantics require a measured point.", "Deleting a step also deletes its parameter slots and related links, and cannot be undone. Confirm the impact first.", "Client-side checks cover only visible rules; validate process suitability and PLC behavior against the site and server checks."] }
    ]
  },
  approvals: {
    "zh-CN": [
      { title: "定位待审版本", items: ["列表只显示审核中的配方；可按编码、名称、产品或待审节点搜索。刷新可重新取得当前待办。", "选择配方后，先核对送审版本、待审节点提示及其与生效版本的差异。审核内容以只读方式展示工艺流和参数矩阵。", "签署链展示已完成及待处理节点；当前可处理节点由审批链配置和当前审核状态共同决定。"] },
      { title: "提交审核决定", items: ["确认自己是当前节点要求的角色后，再选择通过或驳回。", "通过会将处理结果写入该审核节点，后续是否还有节点取决于审批链。驳回前填写清楚、可追溯的原因。", "完成后继续处理列表中的下一项；需要编辑配方内容时跳转设计器，而不是在审核视图中修改。"] },
      { title: "权限与边界", items: ["页面按当前待审节点显示可用操作；角色或审批规则以服务端最终校验为准。", "不要把节点名称理解成固定角色顺序；不同配方可能使用不同审批链。"] }
    ],
    en: [
      { title: "Locate a pending version", items: ["The list shows recipes in review. Search by code, name, product, or pending step, and refresh to retrieve the current queue.", "Select a recipe and verify the submitted version, pending-step prompt, and differences from the approved version. The process flow and parameter matrix are read-only here.", "The signature chain shows completed and pending steps. The actionable step depends on the configured chain and current review state."] },
      { title: "Submit a decision", items: ["Verify that your role matches the current review step before approving or rejecting.", "Approval records a decision for that step; whether another step follows depends on the chain. Provide a clear, traceable reason when rejecting.", "Continue with the next item after submitting. Navigate to the designer if recipe content needs changes; do not expect to edit it in the review view."] },
      { title: "Access and boundaries", items: ["Available actions are shown for the current pending step; the server performs the final authorization and validation.", "Step names are configurable and do not imply a fixed role sequence across all recipes."] }
    ]
  },
  approvalChains: {
    "zh-CN": [
      { title: "配置审核顺序", items: ["从列表选择现有审批链，或创建新链；核对链编码、名称、默认及启用状态。已有链的编码不可修改。", "按实际顺序添加审核节点，配置节点名称、要求角色及通过/驳回含义；使用上移、下移调整顺序，删除前确认不再需要该节点。", "同一角色不能在一条链中重复出现；页面不允许将工艺工程师或操作员设为审核角色。"] },
      { title: "保存与维护", items: ["保存前逐项检查节点次序、角色和提示语，再按要求完成电子签名。", "删除审批链也需电子签名；删除前检查是否仍被配方引用，页面本身不保证可删除状态。", "保存后回到配方配置核对其选用的审批链。停用链与默认链的作用应结合页面状态和服务端规则确认。"] },
      { title: "版本影响", items: ["修改审批链只影响之后提交的配方版本；已经进入审核的版本继续使用提交时记录的审批配置。", "若审批路径有变化，先与流程负责人确认生效时机，避免正在审核的版本与新版本规则混淆。"] }
    ],
    en: [
      { title: "Configure review order", items: ["Select an existing chain or create one. Verify its code, name, default flag, and enabled state. An existing chain's code cannot be changed.", "Add review steps in the intended order and set each step's name, required role, and approve/reject meaning. Use the move controls to reorder; confirm before removing a step.", "A role cannot appear twice in one chain. Process engineers and operators are not available as review roles in this page."] },
      { title: "Save and maintain", items: ["Check every step's order, role, and prompt before saving, then complete the e-signature requirement.", "Deleting a chain also requires an e-signature. Check whether recipes still reference it; the page does not guarantee that every chain is deletable.", "After saving, verify which chain each recipe uses. Confirm the effects of disabled and default chains against their displayed state and server rules."] },
      { title: "Version impact", items: ["Changes apply to recipe versions submitted in the future. Versions already in review retain the approval configuration captured at submission.", "Coordinate the effective time of workflow changes with the process owner to avoid confusion between in-review and new submissions."] }
    ]
  },
  batches: {
    "zh-CN": [
      { title: "查询与跟踪", items: ["按批次号、配方或设备关键字搜索，并使用状态筛选定位批次；可通过表头排序和分页浏览结果。", "列表显示批次状态、配方及设备等摘要信息；选择一行进入批次监控，查看实时运行、报警和握手信息。", "注意“待检终样”等状态表示流程节点，不等同于运行中或已放行。"] },
      { title: "创建生产批次", items: ["选择已批准的主配方，核对批次号、主设备、缩放因子、产出批号和投料物料批。", "多单元配方还需为单元绑定设备；设备不兼容时不可选。若设备正在占用，页面提示可创建快照但启动需等待设备空闲。", "同一设备被多个单元共用时会串行执行；提交前检查设备分配是否符合现场排程。"] },
      { title: "提交前检查", items: ["确认所选配方版本、生效状态、设备和物料批均正确，再创建批次。", "批次号重复检查可能只覆盖当前已加载记录；以服务端创建结果为准。"] }
    ],
    en: [
      { title: "Search and track", items: ["Search by batch number, recipe, or equipment keyword, then filter by status. Use column sorting and pagination to browse results.", "The list summarizes batch status, recipe, and equipment. Select a row to open its monitor for live execution, alarms, and handshake details.", "A state such as Final sample pending is a workflow stage; it does not mean running or released."] },
      { title: "Create a production batch", items: ["Select an approved master recipe and verify the batch number, primary equipment, scale factor, output lot, and input material lots.", "For multi-unit recipes, assign equipment to each unit. Incompatible equipment cannot be selected. If equipment is occupied, the page indicates that a snapshot can be created but starting must wait until it is free.", "When multiple units share equipment, execution is serialized. Check the assignment against the production schedule before submitting."] },
      { title: "Before submitting", items: ["Verify the selected recipe version, approval state, equipment, and material lots before creating the batch.", "Duplicate-number checks may only cover records currently loaded; the server response is authoritative."] }
    ]
  },
  batchMonitor: {
    "zh-CN": [
      { title: "读取实时状态", items: ["先核对批次号、配方快照、设备和批次状态，确认没有打开错误批次。", "在运行、趋势、报警和履历页签间切换查看详情；多设备批次可选择车道，选择工步后质检数据会随之联动。", "查看设定参数矩阵、PLC 写参计划、趋势、报警和四步握手履历；趋势/履历可能截取部分数据，留意页面显示的总量提示。"] },
      { title: "执行控制", items: ["页面按批次状态和角色显示可用操作：创建或故障状态可能允许启动/重新排队，运行或排队可保持，保持可恢复；运行、排队或保持状态可中止。", "出现待人工确认时按工艺要求确认；主管可执行页面提供的跳步操作。跳步仅在允许的工步/握手相位出现。", "按权限取样，并核对样品关联的工步与批次。每次控制前重新确认目标批次、设备和当前状态。"] },
      { title: "安全与验证", items: ["运行控制会影响实际批次；按钮可见只代表前端允许发起，不代表设备或服务端已成功执行。操作后检查状态和履历变化。", "报警列表可能只加载最近一段数据；要核对完整记录时查看电子批记录。"] }
    ],
    en: [
      { title: "Read live status", items: ["Verify the batch number, recipe snapshot, equipment, and batch state before reviewing or acting.", "Switch among Run, Trend, Alarms, and History. For multi-equipment batches, select a lane; selecting a step also updates the quality data shown.", "Review the setpoint matrix, PLC write plan, trends, alarms, and four-step handshake history. Trend and history data may be truncated; check any total-count indicator."] },
      { title: "Control execution", items: ["Available controls depend on state and role: Created or Faulted may allow start or requeue; Running or Queued may allow hold; Held may allow resume; Running, Queued, or Held may allow abort.", "Confirm a pending manual step when required by the process. Supervisors may have a skip-step action, shown only for permitted steps or handshake phases.", "Take samples when authorized and verify the linked step and batch. Recheck the target batch, equipment, and current state before each control action."] },
      { title: "Safety and verification", items: ["Run controls affect an active batch. A visible button only means the client can submit a request; verify the resulting state and history to confirm execution.", "The alarm list may contain only recent entries. Open the electronic batch record when the complete record is needed."] }
    ]
  },
  batchRecord: {
    "zh-CN": [
      { title: "核对归档内容", items: ["先确认批次抬头和归档配方快照，再依次检查工艺工步、参数结果、握手时序、报警、配方漂移和电子签名。", "查看物料谱系，确认投料批与产出追溯关系；查看实验室样品、判定结果及关联工步。", "可返回批次监控继续查看实时信息，或使用打印和 PDF/A 导出形成归档副本。"] },
      { title: "质量处置", items: ["批次完成后，具备质量权限的用户可按页面入口放行或拒收；拒收需填写原因。", "对待判实验室样品可判定合格或不合格；不合格需填写说明。提交前核对样品、批次和检测结果。", "处置是质量流程中的关键记录；提交前确认选择和说明，提交后查看页面反馈。"] },
      { title: "追溯边界", items: ["此页面用于审阅归档记录，不应把它当成修改已归档工艺或实时参数的入口。", "放行条件和意见是否必填以服务端最终校验为准；需要解释偏差时应填写清晰说明。"] }
    ],
    en: [
      { title: "Review the archived record", items: ["Verify the batch header and archived recipe snapshot, then review process steps, parameter results, handshake timing, alarms, recipe drift, and signatures.", "Inspect material genealogy for input-to-output traceability. Review lab samples, dispositions, and their linked steps.", "Return to the batch monitor for live information, or use Print and PDF/A export to create an archive copy."] },
      { title: "Quality disposition", items: ["After batch completion, a quality-authorized user can release or reject through the available action. Rejection requires a reason.", "Pending lab samples can be marked pass or fail; a failure requires an explanation. Verify the sample, batch, and test result before submitting.", "Disposition is a key quality record. Confirm the selected action and explanation, then review the result shown by the page."] },
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
      { title: "拆分子批", items: ["仅当前批状态为 Open 时显示拆分入口。填写子批批号和拆分数量，提交前核对父批及数量。", "成功后页面跳转到新子批，可继续核对其谱系关系。", "页面表单未展示父批剩余量、批号唯一性等完整校验；严格以服务端提交结果为准。"] },
      { title: "追溯用途", items: ["通过祖先链追原料来源，通过子批和生产批次查看拆分及投料去向。", "若关系或数量与业务记录不符，先核对相关物料批和生产批记录，再进行后续操作。"] }
    ],
    en: [
      { title: "Navigate the genealogy", items: ["The header identifies the current lot, material, source, state, and quantity. Verify the current node before taking action.", "Review ancestor lots for origin and child lots for splits. Linked production batches show consumption role, batch, and quantity.", "Select an ancestor or child link to move to that node; return to the material-lot list when finished."] },
      { title: "Split a child lot", items: ["The split action appears only when the current lot is Open. Enter a child lot number and split quantity, then verify the parent lot and quantity.", "After success, the page opens the new child lot so you can verify the genealogy link.", "The form does not expose all checks, such as remaining parent quantity or lot-number uniqueness; rely on the server response."] },
      { title: "Traceability use", items: ["Follow ancestors to trace source material and children or production batches to review splits and consumption destinations.", "If a relationship or quantity differs from business records, verify the related material lots and batch record before proceeding."] }
    ]
  },
  alarms: {
    "zh-CN": [
      { title: "筛选与定位", items: ["页面默认显示未确认报警；可切换查看全部。按批次、报警代码或说明搜索，使用排序和分页查看结果。", "逐项检查时间、批次、工步、代码、级别、说明及确认人；选择报警行可跳到关联批次。", "先处理当前页高优先级或影响运行的报警，再查看其他分页；批量确认只作用于当前页。"] },
      { title: "确认报警", items: ["单条确认前先核对报警详情及关联批次，确认现场或工艺问题已按流程处置。", "确认本页会批量确认本页未确认项，操作前检查当前筛选和页码，并在确认提示中再次核对。", "确认只更新确认状态；报警记录仍保留供审计和追溯。"] },
      { title: "注意事项", items: ["报警确认不等于故障自动消除；需要回到批次监控或设备页检查问题是否真正恢复。", "分页和批量操作范围以当前显示页面为准；若请求部分失败，刷新并核对各条记录状态。"] }
    ],
    en: [
      { title: "Filter and locate", items: ["The page defaults to unacknowledged alarms; switch to All to include acknowledged entries. Search by batch, alarm code, or description, then sort and paginate.", "Review time, batch, step, code, severity, description, and acknowledging user. Select a row to open its associated batch.", "Address high-priority or run-impacting alarms on the current page first; bulk acknowledgement applies only to that page."] },
      { title: "Acknowledge alarms", items: ["Before acknowledging an individual alarm, verify its details and batch and ensure the issue has been handled according to procedure.", "Acknowledge current page affects unacknowledged entries on the displayed page. Check the filters and page number, then verify the confirmation prompt.", "Acknowledgement changes the acknowledgement state; the alarm record remains available for audit and traceability."] },
      { title: "Important", items: ["Acknowledgement does not mean the fault has cleared. Return to the batch monitor or equipment page to verify recovery.", "Pagination and bulk-action scope follow the current page. If a request partially fails, refresh and check each record's state."] }
    ]
  },
  equipment: {
    "zh-CN": [
      { title: "设备与相模板", items: ["在设备和相模板页签间切换；设备列表可按编码、名称或主机搜索。核对协议、端口、型号、设备类、启用状态及占用情况。", "按权限查看或编辑设备、测试连接、校验点表；页面也可能提供仿真故障入口。相库按设备类组织模板，可搜索、添加、编辑或删除。", "设备表单包含连接参数、设备类、握手看门狗/点表、实测点和写参槽；OPC UA 还有安全选项。编辑前核对目标设备和协议。"] },
      { title: "变更前后检查", items: ["保存连接参数或点表前与设备配置及现场资料核对；修改后按页面入口测试连接或校验点表。", "相模板供工艺配置使用；调整模板后确认目标设备类及相关配方是否需要复核。", "只有管理员或工艺工程师可编辑相库；列表提供的其他操作仍受角色和服务端校验限制。"] },
      { title: "安全边界", items: ["连接参数、点表和模板可能影响实际设备执行，必须确认设备处于允许维护的状态并遵循授权流程。", "仿真故障入口的可见性不代表真实 PLC 支持该操作；确认设备协议和环境后再操作。"] }
    ],
    en: [
      { title: "Equipment and phase templates", items: ["Switch between Equipment and Phase templates. Search equipment by code, name, or host. Verify protocol, port, model, equipment class, enabled state, and occupancy.", "Depending on access, view or edit equipment, test a connection, validate a point map, or use a simulation fault action. The phase library organizes templates by equipment class and supports search and maintenance.", "Equipment settings include connection parameters, class, handshake watchdog and points, measured points, and write slots; OPC UA also has security options. Verify the target and protocol before editing."] },
      { title: "Before and after changes", items: ["Compare connection settings and point maps with approved device documentation before saving; afterward, use the available connection test or point-map validation.", "Phase templates are used in process configuration. After changing one, verify its equipment class and whether related recipes need review.", "Only administrators and process engineers can edit the phase library. Other actions remain subject to role and server checks."] },
      { title: "Safety boundary", items: ["Connection settings, point maps, and templates can affect real equipment. Confirm the device is in an approved maintenance state and follow authorization procedures.", "A visible simulation fault action does not prove that a real PLC supports it. Verify the protocol and environment before use."] }
    ]
  },
  audit: {
    "zh-CN": [
      { title: "搜索审计记录", items: ["输入用户、动作或详情关键字；按实体类型筛选主配方、批次、设备、相模板、用户、物料批、实验室样品或报警。", "表格默认按时间倒序分页；检查操作者、动作、实体、时间和详情，选择可跳转记录查看关联对象。", "实验室样品审计记录在详情中解析到批次后才能跳转；无法解析时可使用批次号或详情关键字搜索。"] },
      { title: "还原操作背景", items: ["用实体类型和关键字缩小范围，再沿关联对象进入配方、批次或设备页面核对当前业务状态。", "结合相邻时间的审计记录还原操作顺序；不要仅凭单条摘要推断完整处置过程。"] },
      { title: "只读与筛选范围", items: ["审计记录用于追溯，本页不提供修改或删除审计数据的操作。", "页面没有时间范围控件；如需限定区间，应结合可用搜索条件或其他审计工具，不要假定列表已按自定义时间过滤。"] }
    ],
    en: [
      { title: "Search audit entries", items: ["Search by user, action, or detail keyword. Filter by entity type: master recipe, batch, equipment, phase template, user, material lot, lab sample, or alarm.", "The table is paginated and defaults to descending time order. Review actor, action, entity, time, and details; select a linked entry to open its related object.", "A lab-sample entry can navigate only when its details resolve to a batch. Otherwise, search by batch number or detail text."] },
      { title: "Reconstruct context", items: ["Narrow results by entity and keyword, then follow links to the recipe, batch, or equipment page to verify current business state.", "Use nearby audit entries to reconstruct sequence; a single summary may not show the complete disposition."] },
      { title: "Read-only and filter scope", items: ["Audit entries support traceability; this page provides no edit or delete action.", "There is no time-range control on this page. Use available search conditions or another audit tool when a specific interval is required; do not assume a custom time filter is active."] }
    ]
  },
  users: {
    "zh-CN": [
      { title: "账号与角色", items: ["按登录名或显示名搜索用户；列表显示登录名、显示名、角色和启用状态。", "新建账号时填写登录名、显示名、密码并分配岗位所需角色；登录名至少 3 位，密码至少 8 位。", "编辑时登录名不可更改；密码留空表示不修改。按最小权限原则分配角色，离岗或不再使用的账号应按流程停用。"] },
      { title: "备份与维护", items: ["备份区显示自动备份启用状态、UTC 时间、保留份数、下次执行时间、目录和文件清单。刷新可重新读取状态。", "可发起立即备份、数据库维护，或在要求电子签名时下载 SQLite 整库。操作前确认目标环境、签名身份和存储位置。", "备份保存在服务器本机；超过保留份数的旧快照会被删除。下载后按组织安全规则保护数据库文件。"] },
      { title: "重要限制", items: ["此页面仅管理员可访问；账号、权限和数据库维护属于高影响操作，提交前复核目标用户和操作范围。", "本页未提供备份恢复或完整性验证入口；显示备份文件不等于已经验证该备份可恢复。"] }
    ],
    en: [
      { title: "Accounts and roles", items: ["Search by login name or display name. The list shows login name, display name, roles, and enabled state.", "When creating an account, enter a login name, display name, password, and job-required roles. Login names require at least three characters and passwords at least eight.", "An existing login name cannot be changed. Leave the password blank when editing to keep it unchanged. Apply least privilege and disable accounts no longer in use according to policy."] },
      { title: "Backups and maintenance", items: ["The backup area shows automatic-backup state, UTC timestamps, retention count, next run, directory, and file list. Refresh to retrieve current status.", "You can request an immediate backup, database maintenance, or a signed download of the SQLite database when required. Verify the environment, signing identity, and storage location first.", "Backups are stored on the server itself; older snapshots beyond the retention count are deleted. Protect downloaded database files according to security policy."] },
      { title: "Important limits", items: ["This page is administrator-only. Account, permission, and database-maintenance actions have significant impact; verify the target and scope before submitting.", "There is no backup-restore or integrity-validation action here. A listed backup file is not proof that it has been tested for recovery."] }
    ]
  },
  login: {
    "zh-CN": [
      { title: "登录步骤", items: ["输入已分配的用户名和密码，再提交登录；用户名或密码为空时页面会阻止提交。", "登录成功后优先返回原先请求的站内页面；没有合法返回地址时进入运行总览。", "仅开发环境显示演示账号快捷填入；生产环境不会显示该入口。不要将演示凭证用于生产系统。"] },
      { title: "登录失败时", items: ["重新确认用户名拼写、键盘输入状态和密码；避免连续猜测密码。", "若仍无法登录，请联系管理员核对账号启用状态和权限。具体失败原因、锁定规则及密码策略由认证服务决定。"] },
      { title: "账号安全", items: ["不要共享密码；离开共享设备前退出账号。", "通过可信系统地址登录，收到异常登录提示时按组织安全流程处理。"] }
    ],
    en: [
      { title: "Sign-in steps", items: ["Enter your assigned username and password, then submit. The page blocks submission if either field is empty.", "After successful sign-in, the app returns to the originally requested in-app page when valid; otherwise it opens the dashboard.", "Demo-account shortcuts appear only in development, not production. Never use demo credentials in a production system."] },
      { title: "If sign-in fails", items: ["Recheck the username spelling, keyboard input state, and password; avoid repeated guessing.", "If the issue continues, ask an administrator to verify that the account is enabled and authorized. Failure details, lockout rules, and password policy are controlled by the authentication service."] },
      { title: "Account security", items: ["Do not share your password; sign out before leaving a shared device.", "Use a trusted system address and follow organizational security procedures if you receive an unexpected sign-in alert."] }
    ]
  },
  notFound: {
    "zh-CN": [
      { title: "确认地址", items: ["页面会显示当前访问地址；检查路径拼写、批次或资源 ID 是否正确。", "如果地址来自书签或他人分享，可能已过期、目标已变更，或当前账号无权访问。页面无法仅凭 404 区分这些情况。"] },
      { title: "继续导航", items: ["选择“返回运行总览”进入可用入口；也可使用“返回上一页”回到浏览历史中的上一个页面。", "若没有浏览器历史记录，返回上一页会改为回到运行总览。之后可通过导航菜单重新进入目标功能。"] }
    ],
    en: [
      { title: "Check the address", items: ["The page displays the current address. Verify the path spelling and any batch or resource ID.", "A bookmarked or shared address may be outdated, its target may have changed, or access may be restricted. A 404 page alone cannot distinguish these cases."] },
      { title: "Continue navigating", items: ["Choose Go to dashboard to open an available entry point, or use Back to return to the previous page in browser history.", "If no browser history is available, Back returns to the dashboard. Use the navigation menu to locate the intended feature."] }
    ]
  }
} as const;

type PageGuideKey = keyof typeof PAGE_GUIDES;
const props = defineProps<{ guideKey: PageGuideKey }>();
const { locale } = useI18n({ useScope: "global" });
const open = ref(false);
const isEnglish = computed(() => locale.value === "en");
const guide = computed<GuideCopy>(() => PAGE_GUIDES[props.guideKey][isEnglish.value ? "en" : "zh-CN"]);
const details = computed<readonly GuideSection[]>(
  () => PAGE_GUIDE_DETAILS[props.guideKey][isEnglish.value ? "en" : "zh-CN"]
);
const labels = computed(() => isEnglish.value
  ? { button: "Page guide", steps: "Quick start", note: "Important note" }
  : { button: "本页说明", steps: "快速操作", note: "重要提醒" });
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
</style>
