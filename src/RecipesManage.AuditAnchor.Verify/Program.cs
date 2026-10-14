using System.Globalization;
using System.Text.Json;
using RecipesManage.AuditAnchor.Protocol;
using RecipesManage.AuditAnchor.Verify;

return await AuditAnchorVerifyCommand.RunAsync(args);

internal static class AuditAnchorVerifyCommand
{
    private const long MaxInputBytes = 16 * 1024 * 1024;
    private const long MaxTotalInputBytes = 128 * 1024 * 1024;
    private const int MaxInputFiles = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
        {
            PrintUsage();
            return args.Contains("--help", StringComparer.Ordinal) ? 0 : 2;
        }

        try
        {
            var options = ParseOptions(args);
            var pages = await LoadPagesAsync(options.InputPath);
            using var certificates = AuditAnchorPublicCertificateCatalog.Load(options.CertificatesPath);
            var expectedHead = ParseExpectedHead(options.ExpectedHead);
            var result = AuditAnchorReceiptVerifier.Verify(
                pages,
                certificates,
                expectedHead?.Sequence,
                expectedHead?.Hash);

            Console.WriteLine($"VALID source={result.SourceId} receipts={result.ReceiptCount} latestSequence={result.LatestSequence}");
            Console.WriteLine($"HeadReceiptHash={result.HeadReceiptHash ?? "(empty ledger)"}");
            Console.WriteLine($"SigningKeyIds={string.Join(",", result.SigningKeyIds)}");
            Console.WriteLine(expectedHead is null
                ? "Completeness matches the latestSequence reported by the exported pages; no independent rollback checkpoint was supplied."
                : "The complete chain head matches the independently pinned checkpoint.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"INVALID: {error.Message}");
            return 1;
        }
    }

    private static VerifyOptions ParseOptions(string[] args)
    {
        string? input = null;
        string? certificates = null;
        string? expectedHead = null;

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {option}.");
            var value = args[++index];
            switch (option)
            {
                case "--input" when input is null:
                    input = value;
                    break;
                case "--certificates" when certificates is null:
                    certificates = value;
                    break;
                case "--expected-head" when expectedHead is null:
                    expectedHead = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown or repeated option: {option}.");
            }
        }

        if (input is null || certificates is null)
            throw new ArgumentException("Both --input and --certificates are required.");
        return new VerifyOptions(input, certificates, expectedHead);
    }

    private static async Task<IReadOnlyList<AuditAnchorReceiptPage>> LoadPagesAsync(string path)
    {
        string[] files = File.Exists(path)
            ? [Path.GetFullPath(path)]
            : Directory.Exists(path)
                ? Directory.GetFiles(path, "*.json", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray()
                : throw new FileNotFoundException("Receipt input file or directory was not found.", path);

        if (files.Length == 0)
            throw new InvalidDataException("Receipt input directory contains no JSON pages.");
        if (files.Length > MaxInputFiles)
            throw new InvalidDataException($"Receipt input exceeds the {MaxInputFiles}-file limit.");

        var pages = new List<AuditAnchorReceiptPage>(files.Length);
        long totalBytes = 0;
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            if (info.Length is <= 0 or > MaxInputBytes)
                throw new InvalidDataException($"Receipt page size is invalid: {info.Name}.");
            totalBytes += info.Length;
            if (totalBytes > MaxTotalInputBytes)
                throw new InvalidDataException($"Receipt pages exceed the {MaxTotalInputBytes}-byte input limit.");
            await using var stream = File.OpenRead(file);
            var page = await JsonSerializer.DeserializeAsync<AuditAnchorReceiptPage>(stream, JsonOptions);
            if (page is null)
                throw new InvalidDataException($"Receipt page is empty: {info.Name}.");
            pages.Add(page);
        }
        return pages;
    }

    private static ExpectedHead? ParseExpectedHead(string? value)
    {
        if (value is null) return null;
        var parts = value.Split(':');
        if (parts.Length != 2 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ||
            sequence <= 0 || parts[1].Length != 64 || !parts[1].All(Uri.IsHexDigit))
            throw new ArgumentException("--expected-head must be <positive-sequence>:<64-hex-receipt-hash>.");
        return new ExpectedHead(sequence, parts[1]);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: RecipesManage.AuditAnchor.Verify --input <receipt-page.json|directory> --certificates <certificate.pem|directory> [--expected-head <sequence>:<receipt-hash>]");
        Console.WriteLine("Pages must together contain the complete chain from sequence 1 through latestSequence.");
        Console.WriteLine("--expected-head compares the verified head with an independently retained checkpoint.");
    }

    private sealed record VerifyOptions(string InputPath, string CertificatesPath, string? ExpectedHead);
    private sealed record ExpectedHead(long Sequence, string Hash);

}
