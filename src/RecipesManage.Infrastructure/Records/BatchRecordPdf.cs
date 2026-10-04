using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Infrastructure.Records;

public sealed class BatchRecordPdf : IBatchRecordPdf
{
    static BatchRecordPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        TryRegisterCjkFont();
    }

    public byte[] Render(BatchRecordDto record)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(t => t.FontSize(9).FontFamily(_fontFamily).FontColor(Colors.Black));
                page.Header().Column(col =>
                {
                    col.Item().Text("电子批记录 / Electronic Batch Record").FontSize(16).Bold();
                    col.Item().Text($"批次 {record.BatchNo} · {record.Snapshot.RecipeName} v{record.Snapshot.VersionNumber}")
                        .FontSize(11);
                    col.Item().Text($"生成 {record.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC · 快照 {record.SnapshotIntegrity} · 状态 {record.Status}")
                        .FontColor(Colors.Grey.Darken2);
                });
                page.Footer().AlignRight().Text(t =>
                {
                    t.Span("BRMES 四步握手归档 · PDF/A · ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
                page.Content().PaddingVertical(12).Column(col =>
                {
                    col.Spacing(10);
                    col.Item().Element(c => KeyValues(c, record));
                    col.Item().Element(c => Section(c, "配方电子签名", table =>
                    {
                        table.ColumnsDefinition(d =>
                        {
                            d.ConstantColumn(70);
                            d.ConstantColumn(70);
                            d.RelativeColumn();
                            d.RelativeColumn();
                            d.RelativeColumn(2);
                        });
                        HeaderRow(table, "审核节点", "结论", "签署人", "时间", "含义");
                        foreach (var a in record.RecipeApprovals)
                            BodyRow(table, a.Title, a.Decision.ToString(), a.ReviewerName ?? "", Format(a.DecidedAt), a.Meaning ?? "");
                    }));
                    col.Item().Element(c => Section(c, "批次执行电子签名", table =>
                    {
                        table.ColumnsDefinition(d =>
                        {
                            d.ConstantColumn(70);
                            d.ConstantColumn(70);
                            d.RelativeColumn();
                            d.RelativeColumn(2);
                            d.RelativeColumn();
                            d.RelativeColumn(1.4f);
                        });
                        HeaderRow(table, "动作", "签署人", "时间", "含义", "意见", "证据摘要校验");
                        var esigns = record.Esigns ?? [];
                        if (esigns.Count == 0)
                            BodyRow(table, "—", "—", "—", "尚无启动 / 保持 / 跳步 / 放行签署", "旧批次仅有审计动作码", "—");
                        else
                            foreach (var e in esigns)
                                BodyRow(table, e.Action, e.UserName ?? "", Format(e.At), e.Meaning,
                                    e.Extra ?? "", BatchSignatureIntegrityLabel(e.Integrity, e.ContentHash));
                    }));
                    col.Item().Element(c => Section(c, "批次放行电子签名", table =>
                    {
                        table.ColumnsDefinition(d =>
                        {
                            d.ConstantColumn(90);
                            d.RelativeColumn();
                            d.RelativeColumn();
                            d.RelativeColumn(2);
                        });
                        HeaderRow(table, "结论", "签署人", "时间", "意见");
                        if (record.Status is BatchStatus.Released or BatchStatus.DispositionRejected)
                            BodyRow(table, record.Status.ToString(), record.ReleasedBy ?? "", Format(record.ReleasedAt), record.ReleaseComment ?? "");
                        else
                            BodyRow(table, record.Status.ToString(), "—", "—", "待质量对照握手归档与质检后放行");
                    }));
                    col.Item().Element(c => Section(c, "物料投料与产出", table =>
                    {
                        table.ColumnsDefinition(d =>
                        {
                            d.ConstantColumn(70);
                            d.RelativeColumn();
                            d.RelativeColumn();
                            d.ConstantColumn(80);
                            d.ConstantColumn(70);
                        });
                        HeaderRow(table, "角色", "物料批", "物料", "数量", "生产批次");
                        var materials = record.Materials ?? [];
                        if (materials.Count == 0)
                            BodyRow(table, "—", record.Snapshot.LotNumber ?? "未绑定谱系", "—", "—", record.BatchNo);
                        else
                            foreach (var m in materials)
                                BodyRow(table, m.Role.ToString(), m.LotNumber, m.MaterialCode, m.Quantity?.ToString("0.###") ?? "—", m.BatchNo ?? record.BatchNo);
                    }));
                    col.Item().Element(c => Section(c, "实验室样品（LIMS）", table =>
                    {
                        table.ColumnsDefinition(d =>
                        {
                            d.ConstantColumn(80);
                            d.ConstantColumn(70);
                            d.ConstantColumn(70);
                            d.RelativeColumn();
                            d.RelativeColumn();
                        });
                        HeaderRow(table, "样品", "类型", "判定", "取样人", "意见");
                        var labs = record.LabSamples ?? [];
                        if (labs.Count == 0)
                            BodyRow(table, "—", "—", "—", "—", "本批次无实验室样品（PLC 测点见归档质检）");
                        else
                            foreach (var s in labs)
                                BodyRow(table, s.SampleCode, s.SampleType.ToString(), s.Disposition.ToString(), s.TakenBy, s.Comment ?? "");
                    }));
                    col.Item().Element(c => Section(c, "化验判定电子签名与内容摘要", table =>
                    {
                        table.ColumnsDefinition(d =>
                        {
                            d.ConstantColumn(58);
                            d.ConstantColumn(62);
                            d.ConstantColumn(78);
                            d.RelativeColumn(1.2f);
                            d.ConstantColumn(60);
                            d.RelativeColumn(2f);
                        });
                        HeaderRow(table, "样品", "签署人", "签署时间", "签署含义", "校验", "SHA-256");
                        var labs = record.LabSamples ?? [];
                        if (labs.Count == 0)
                            BodyRow(table, "—", "—", "—", "—", "—", "本批次无实验室样品");
                        else
                            foreach (var sample in labs)
                            {
                                var signature = sample.DispositionSignature;
                                BodyRow(table,
                                    sample.SampleCode,
                                    signature?.SignerName ?? "—",
                                    Format(signature?.At),
                                    signature?.Meaning ?? "—",
                                    SignatureIntegrityLabel(signature?.Integrity, sample.Disposition),
                                    signature?.ContentHash ?? "—");
                            }
                    }));
                    col.Item().Element(c => Section(c, "ISA-88 控制配方快照", table =>
                    {
                        table.ColumnsDefinition(d =>
                        {
                            d.ConstantColumn(50);
                            d.RelativeColumn();
                            d.RelativeColumn();
                            d.ConstantColumn(70);
                            d.RelativeColumn(2);
                        });
                        HeaderRow(table, "Phase", "工步", "Unit Procedure", "结果", "设定值");
                        foreach (var step in record.Snapshot.Steps)
                        {
                            var outcome = (record.StepExecutions.FirstOrDefault(s => s.StepId == step.StepId)?.Outcome
                                ?? StepOutcome.Pending).ToString();
                            var setpoints = string.Join("；", step.Parameters.Select(p => $"{p.Name}={p.Setpoint}{p.EngineeringUnit}"));
                            BodyRow(table, step.Code, step.Name, step.UnitProcedure ?? Isa88.DefaultUnitProcedure, outcome, setpoints);
                        }
                    }));
                    col.Item().Element(c => SetpointMatrixSection(c, record));
                    col.Item().Element(c => WritePlanSection(c, record));
                    col.Item().Element(c => Section(c, "四步握手时序（禁止盲写）", table =>
                    {
                        table.ColumnsDefinition(d =>
                        {
                            d.ConstantColumn(120);
                            d.ConstantColumn(50);
                            d.ConstantColumn(90);
                            d.ConstantColumn(60);
                            d.RelativeColumn();
                        });
                        HeaderRow(table, "时间", "工步", "阶段", "动作", "说明");
                        foreach (var row in record.Handshake.Take(80))
                            BodyRow(table, Format(row.At), row.StepCode, row.Phase, row.Kind, row.Detail ?? "");
                    }));
                    col.Item().Element(c => Section(c, "归档质检", table =>
                    {
                        table.ColumnsDefinition(d =>
                        {
                            d.RelativeColumn();
                            d.RelativeColumn();
                            d.ConstantColumn(80);
                            d.RelativeColumn();
                        });
                        HeaderRow(table, "工步", "测点", "实测", "规格");
                        foreach (var exec in record.StepExecutions.Where(s => !string.IsNullOrWhiteSpace(s.QualityJson)))
                        {
                            try
                            {
                                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(exec.QualityJson!);
                                if (parsed is null) continue;
                                foreach (var (tag, value) in parsed)
                                    BodyRow(table, exec.StepCode, tag, value.ToString("0.##"), "");
                            }
                            catch
                            {
                                BodyRow(table, exec.StepCode, "raw", exec.QualityJson ?? "", "");
                            }
                        }
                    }));
                });
            });
        })
        .WithMetadata(new DocumentMetadata
        {
            Title = $"eBR {record.BatchNo}",
            Author = "BRMES",
            Subject = "Batch Recipe Management & Execution System electronic batch record",
            Creator = "BRMES",
            Language = "zh-CN"
        })
        .WithSettings(PdfSettings())
        .GeneratePdf();
    }

    private static DocumentSettings PdfSettings() => new()
    {
        PdfA = true
    };

    private static string _fontFamily = "Helvetica";

    private static void TryRegisterCjkFont()
    {
        foreach (var path in new[]
                 {
                     @"C:\Windows\Fonts\msyh.ttc",
                     @"C:\Windows\Fonts\msyh.ttf",
                     @"C:\Windows\Fonts\simhei.ttf",
                     @"C:\Windows\Fonts\simsun.ttc",
                     @"C:\Windows\Fonts\arial.ttf"
                 })
        {
            if (!File.Exists(path))
                continue;
            try
            {
                FontManager.RegisterFontWithCustomName("BRMES-CJK", File.OpenRead(path));
                _fontFamily = "BRMES-CJK";
                return;
            }
            catch
            {
                // try next face
            }
        }
    }

    private static void SetpointMatrixSection(IContainer container, BatchRecordDto record)
    {
        var steps = record.Snapshot.Steps.OrderBy(s => s.Ordinal).ToList();
        var names = new List<(string Name, string Unit)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            foreach (var p in step.Parameters)
            {
                var key = $"{p.Name}|{p.EngineeringUnit}";
                if (!seen.Add(key))
                    continue;
                names.Add((p.Name, p.EngineeringUnit));
            }
        }

        if (names.Count == 0)
        {
            Section(container, "控制参数矩阵 / Setpoints", table =>
            {
                table.ColumnsDefinition(d => d.RelativeColumn());
                HeaderRow(table, "内容");
                BodyRow(table, "本快照无设定值参数");
            });
            return;
        }

        Section(container, "控制参数矩阵 / Setpoints（设定 / 归档实测）", table =>
        {
            table.ColumnsDefinition(d =>
            {
                d.RelativeColumn(1.6f);
                d.ConstantColumn(40);
                foreach (var _ in steps)
                    d.RelativeColumn();
            });
            var headers = new List<string> { "参数", "单位" };
            headers.AddRange(steps.Select(s => s.Code));
            HeaderRow(table, headers.ToArray());
            foreach (var n in names)
            {
                var cells = new List<string> { n.Name, n.Unit };
                foreach (var step in steps)
                {
                    var p = step.Parameters.FirstOrDefault(x => x.Name == n.Name && x.EngineeringUnit == n.Unit);
                    if (p is null)
                    {
                        cells.Add("—");
                        continue;
                    }
                    var exec = record.StepExecutions.FirstOrDefault(s => s.StepId == step.StepId);
                    var actual = QualityArchive.EvaluateJson(step, exec?.QualityJson)
                        .FirstOrDefault(r => string.Equals(r.Name, n.Name, StringComparison.Ordinal));
                    cells.Add(QualityArchive.FormatSetpointVsActual(p.Setpoint, actual));
                }
                BodyRow(table, cells.ToArray());
            }
        });
    }

    private static void WritePlanSection(IContainer container, BatchRecordDto record)
    {
        var plan = record.WritePlan;
        if (plan.Count == 0)
            plan = ControlRecipeWritePlan.FromSnapshot(record.Snapshot)
                .Select(i => new PlcWritePlanDto(
                    i.StepId, i.StepCode, i.StepName, i.StepType,
                    i.PlcStepId, i.PlcStepType, i.Parameters, i.WriteToPlc, i.Policy))
                .ToList();

        Section(container, "PLC 写参计划（拓扑顺序，禁止盲写）", table =>
        {
            table.ColumnsDefinition(d =>
            {
                d.ConstantColumn(50);
                d.RelativeColumn();
                d.ConstantColumn(55);
                d.RelativeColumn(2);
                d.RelativeColumn(2);
            });
            HeaderRow(table, "工步", "名称", "Step_ID", "写参", "策略");
            foreach (var item in plan)
            {
                var payload = item.WriteToPlc
                    ? string.Join(" ", item.Parameters
                        .Select((v, i) => (v, i))
                        .Where(p => p.v != 0)
                        .Select(p => $"Param[{p.i}]={p.v:0.###}"))
                    : "禁止写 PLC";
                BodyRow(table, item.StepCode, item.StepName,
                    item.WriteToPlc ? item.PlcStepId.ToString() : "—",
                    payload, item.Policy);
            }
        });
    }

    private static void KeyValues(IContainer container, BatchRecordDto record)
    {
        var units = record.Snapshot.UnitEquipment is { Count: > 0 } map
            ? string.Join("；", map.Select(kv => $"{kv.Key}→{kv.Value.ToString()[..8]}"))
            : "全部使用主设备";
        container.Table(table =>
        {
            table.ColumnsDefinition(d =>
            {
                d.ConstantColumn(90);
                d.RelativeColumn();
                d.ConstantColumn(90);
                d.RelativeColumn();
            });
            Cell(table, "批次号", record.BatchNo);
            Cell(table, "状态", record.Status.ToString());
            Cell(table, "主配方", $"{record.Snapshot.RecipeCode} {record.Snapshot.RecipeName}");
            Cell(table, "完整性", record.SnapshotIntegrity);
            Cell(table, "物料批次", record.Snapshot.LotNumber ?? "—");
            Cell(table, "缩放", (record.Snapshot.ScaleFactor ?? 1).ToString("0.###"));
            Cell(table, "放行人", record.ReleasedBy ?? "待放行");
            Cell(table, "放行时间", Format(record.ReleasedAt));
            Cell(table, "处置证据摘要",
                record.EvidenceHash is { Length: > 0 } hash
                    ? $"v{record.EvidenceHashVersion} SHA-256 {hash}"
                    : "未生成",
                span: 4);
            Cell(table, "单元设备", units, span: 4);
        });
    }

    private static string BatchSignatureIntegrityLabel(string? integrity, string? hash)
    {
        var label = integrity switch
        {
            "Verified" => "摘要匹配",
            "Unbound" => "历史未绑定",
            "Mismatch" => "内容不匹配",
            "Unsupported" => "版本不支持",
            _ => "—"
        };
        return hash is { Length: >= 12 } ? $"{label} {hash[..12]}…" : label;
    }

    private static void Section(IContainer container, string title, Action<TableDescriptor> content)
    {
        container.Column(col =>
        {
            col.Item().Text(title).Bold().FontSize(11);
            col.Item().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Table(content);
        });
    }

    private static void HeaderRow(TableDescriptor table, params string[] cells)
    {
        foreach (var cell in cells)
            table.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text(cell).Bold();
    }

    private static void BodyRow(TableDescriptor table, params string[] cells)
    {
        foreach (var cell in cells)
            table.Cell().Padding(3).Text(cell);
    }

    private static void Cell(TableDescriptor table, string key, string value, uint span = 1)
    {
        table.Cell().Background(Colors.Grey.Lighten4).Padding(3).Text(key);
        var cell = table.Cell();
        if (span > 1)
            cell = cell.ColumnSpan(span - 1);
        cell.Padding(3).Text(value);
    }

    private static string Format(DateTimeOffset? value) =>
        value is null ? "—" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string SignatureIntegrityLabel(string? integrity, LabSampleDisposition disposition) =>
        integrity switch
        {
            "Verified" => "匹配",
            "Unbound" => "历史无摘要",
            "Mismatch" => "不匹配",
            "Unsupported" => "版本未知",
            _ when disposition == LabSampleDisposition.Pending => "待判定",
            _ => "缺少签名"
        };
}
