using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace RecipesManage.Domain.Batches;

/// <summary>Versioned canonical content used to bind a lab-disposition signature to its sample.</summary>
public static class LabSampleSignatureContent
{
    public const int CurrentVersion = 1;

    public static string ComputeHash(LabSample sample) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new ContentV1(
            CurrentVersion,
            sample.Id,
            sample.SampleCode,
            sample.BatchId,
            sample.MaterialLotId,
            sample.ParentSampleId,
            sample.StepId,
            (int)sample.SampleType,
            (int)sample.Disposition,
            sample.ResultsJson,
            sample.TakenBy,
            Format(sample.TakenAt),
            sample.DispositionBy,
            sample.DisposedAt is { } disposedAt ? Format(disposedAt) : null,
            sample.Comment))));

    public static bool Matches(LabSample sample, int version, string hash) =>
        version == CurrentVersion &&
        string.Equals(ComputeHash(sample), hash, StringComparison.OrdinalIgnoreCase);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record ContentV1(
        int Version,
        Guid Id,
        string SampleCode,
        Guid BatchId,
        Guid? MaterialLotId,
        Guid? ParentSampleId,
        Guid? StepId,
        int SampleType,
        int Disposition,
        string? ResultsJson,
        string TakenBy,
        string TakenAt,
        string? DispositionBy,
        string? DisposedAt,
        string? Comment);
}
