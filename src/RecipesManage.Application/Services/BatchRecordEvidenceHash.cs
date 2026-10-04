using System.Security.Cryptography;
using System.Text.Json;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Batches;

namespace RecipesManage.Application.Services;

/// <summary>
/// Stable evidence covered by a quality disposition. Volatile presentation fields (generated time,
/// current recipe drift, batch status, disposition metadata and signature history) are excluded.
/// </summary>
public static class BatchRecordEvidenceHash
{
    public const int CurrentVersion = 1;

    public static string Compute(BatchRecordDto record)
    {
        var evidence = new
        {
            Version = CurrentVersion,
            record.BatchId,
            record.BatchNo,
            record.SnapshotIntegrity,
            Snapshot = SnapshotContent(record.Snapshot),
            StepExecutions = record.StepExecutions
                .OrderBy(s => s.Ordinal).ThenBy(s => s.StepId),
            Handshake = record.Handshake
                .OrderBy(e => e.At).ThenBy(e => e.StepCode, StringComparer.Ordinal)
                .ThenBy(e => e.Phase, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.Detail, StringComparer.Ordinal).ThenBy(e => e.RemainingSeconds),
            Samples = record.Samples
                .OrderBy(s => s.SampledAt).ThenBy(s => s.Tag, StringComparer.Ordinal).ThenBy(s => s.StepId),
            RecipeApprovals = record.RecipeApprovals.OrderBy(a => a.Seq).ThenBy(a => a.Id),
            Alarms = record.Alarms?
                .OrderBy(a => a.RaisedAt).ThenBy(a => a.Id)
                .Select(a => new
                {
                    a.Id,
                    a.BatchId,
                    a.BatchNo,
                    a.StepCode,
                    a.Code,
                    a.Severity,
                    a.Message,
                    a.RaisedAt
                }),
            WritePlan = record.WritePlan?.OrderBy(w => w.StepCode, StringComparer.Ordinal).ThenBy(w => w.StepId),
            Materials = record.Materials?.OrderBy(m => m.Id),
            LabSamples = record.LabSamples?.OrderBy(s => s.TakenAt).ThenBy(s => s.Id)
        };

        var payload = JsonSerializer.SerializeToUtf8Bytes(evidence, SnapshotJson.Options);
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    internal static object SnapshotContent(ControlRecipeSnapshot snapshot) => new
    {
        snapshot.SchemaVersion,
        snapshot.MasterRecipeId,
        snapshot.RecipeVersionId,
        snapshot.VersionNumber,
        snapshot.RecipeCode,
        snapshot.RecipeName,
        snapshot.ProductCode,
        snapshot.ProductName,
        snapshot.FrozenAt,
        snapshot.IntegrityHash,
        snapshot.ScaleFactor,
        snapshot.LotNumber,
        UnitEquipment = snapshot.UnitEquipment?
            .OrderBy(x => x.Key, StringComparer.Ordinal),
        Steps = snapshot.Steps
            .OrderBy(s => s.Ordinal).ThenBy(s => s.StepId)
            .Select(s => new
            {
                s.StepId,
                s.Code,
                s.Name,
                s.Type,
                s.Ordinal,
                s.WatchdogSeconds,
                s.PlcProgramId,
                s.UnitProcedure,
                s.Operation,
                s.EquipmentClassCode,
                Parameters = s.Parameters.OrderBy(p => p.SlotIndex)
            }),
        Edges = snapshot.Edges
            .OrderBy(e => e.FromStepId).ThenBy(e => e.ToStepId)
    };

    public static bool Matches(int? version, string? storedHash, string currentHash) =>
        version == CurrentVersion &&
        storedHash is { Length: 64 } &&
        string.Equals(storedHash, currentHash, StringComparison.OrdinalIgnoreCase);

    public static string Integrity(int? version, string? storedHash, string currentHash)
    {
        if (version is null && storedHash is null)
            return "Unbound";
        if (version is null || string.IsNullOrWhiteSpace(storedHash))
            return "Mismatch";
        if (version != CurrentVersion)
            return "Unsupported";
        return Matches(version, storedHash, currentHash) ? "Verified" : "Mismatch";
    }
}
