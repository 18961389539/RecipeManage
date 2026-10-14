using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RecipesManage.AuditAnchor.Protocol;
using RecipesManage.AuditAnchor.Server;
using RecipesManage.AuditAnchor.Verify;
using RecipesManage.Domain.Identity;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.AuditAnchor.Tests;

public sealed class AuditAnchorTests
{
    [Fact]
    public void Merkle_roots_are_deterministic_ordered_and_stream_separated()
    {
        var rows = new[] { "row-a", "row-b", "row-c" };
        using var first = Tree(AuditAnchorProtocol.AuditLogsStream, rows);
        using var replay = Tree(AuditAnchorProtocol.AuditLogsStream, rows);
        using var reordered = Tree(AuditAnchorProtocol.AuditLogsStream, rows.Reverse());
        using var otherStream = Tree(AuditAnchorProtocol.SignatureRecordsStream, rows);

        Assert.Equal(first.FinishHex(), replay.FinishHex());
        Assert.NotEqual(first.FinishHex(), reordered.FinishHex());
        Assert.NotEqual(first.FinishHex(), otherStream.FinishHex());
        Assert.Equal(3, first.Count);
    }

    [Fact]
    public void Canonical_manifest_ignores_input_stream_order_but_rejects_missing_streams()
    {
        var manifest = CreateManifest();
        var reversed = manifest with { Streams = manifest.Streams.Reverse().ToArray() };

        Assert.Equal(
            AuditAnchorProtocol.CanonicalManifest(manifest),
            AuditAnchorProtocol.CanonicalManifest(reversed));
        Assert.Throws<InvalidDataException>(() =>
            AuditAnchorProtocol.CanonicalManifest(manifest with { Streams = manifest.Streams.Take(1).ToArray() }));
    }

    [Fact]
    public void Receipt_signature_and_hash_detect_payload_changes()
    {
        using var key = RSA.Create(2048);
        var manifest = CreateManifest();
        var receipt = AuditAnchorProtocol.SignReceipt(
            manifest.SourceId, 1, manifest.SnapshotId, AuditAnchorProtocol.ManifestHash(manifest),
            null, DateTimeOffset.UtcNow, "test-key",
            data => key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

        Assert.True(AuditAnchorProtocol.VerifyReceipt(receipt, key, "test-key"));
        Assert.False(AuditAnchorProtocol.VerifyReceipt(receipt with { Sequence = 2 }, key, "test-key"));
        Assert.False(AuditAnchorProtocol.VerifyReceipt(receipt, key, "wrong-key"));
    }

    [Fact]
    public void Offline_verifier_checks_complete_chain_across_key_rotation_and_checkpoint()
    {
        using var firstKey = RSA.Create(2048);
        using var rotatedKey = RSA.Create(2048);
        using var wrongKey = RSA.Create(2048);
        const string firstKeyId = "receiver-key-one";
        const string rotatedKeyId = "receiver-key-two";
        var pages = CreateReceiptPages(firstKey, rotatedKey, firstKeyId, rotatedKeyId);
        var keys = new Dictionary<string, RSA>(StringComparer.OrdinalIgnoreCase)
        {
            [firstKeyId] = firstKey,
            [rotatedKeyId] = rotatedKey
        };
        var head = pages[^1].Entries[^1].Receipt;

        var result = AuditAnchorReceiptVerifier.Verify(pages, keys, 3, head.ReceiptHash);

        Assert.Equal(3, result.ReceiptCount);
        Assert.Equal(3, result.LatestSequence);
        Assert.Equal(head.ReceiptHash, result.HeadReceiptHash);
        Assert.Equal(new[] { firstKeyId, rotatedKeyId }, result.SigningKeyIds.Order().ToArray());
        Assert.Throws<InvalidDataException>(() =>
            AuditAnchorReceiptVerifier.Verify(pages[..1], keys));
        Assert.Throws<InvalidDataException>(() =>
            AuditAnchorReceiptVerifier.Verify(pages, keys, 3, new string('f', 64)));

        var wrongKeys = new Dictionary<string, RSA>(StringComparer.OrdinalIgnoreCase)
        {
            [firstKeyId] = wrongKey,
            [rotatedKeyId] = rotatedKey
        };
        Assert.Throws<InvalidDataException>(() => AuditAnchorReceiptVerifier.Verify(pages, wrongKeys));

        var tampered = pages[0] with
        {
            Entries =
            [
                pages[0].Entries[0] with
                {
                    Manifest = pages[0].Entries[0].Manifest with
                    {
                        Streams = pages[0].Entries[0].Manifest.Streams
                            .Select(x => x with { RootSha256 = new string('f', 64) }).ToArray()
                    }
                },
                pages[0].Entries[1]
            ]
        };
        Assert.Throws<InvalidDataException>(() =>
            AuditAnchorReceiptVerifier.Verify([tampered, pages[1]], keys));
    }

    [Fact]
    public void Public_certificate_catalog_derives_key_id_and_rejects_private_key_material()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"brmes-anchor-certs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=AuditAnchorVerifyTests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(directory, "receiver.pem");
        try
        {
            File.WriteAllText(path, certificate.ExportCertificatePem());
            using var catalog = AuditAnchorPublicCertificateCatalog.Load(path);
            var keyId = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
            Assert.True(catalog.ContainsKey(keyId));
            Assert.Equal(2048, catalog[keyId].KeySize);

            File.WriteAllText(path, certificate.ExportCertificatePem() + key.ExportPkcs8PrivateKeyPem());
            Assert.Throws<InvalidDataException>(() => AuditAnchorPublicCertificateCatalog.Load(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Row_encoding_is_stable_and_distinguishes_null_from_empty()
    {
        var withNull = new AuditLog(null, "operator", "batch.start", "Batch", "batch-1", null);
        var withEmpty = new AuditLog(null, "operator", "batch.start", "Batch", "batch-1", "");

        Assert.Equal(AuditAnchorRowEncoding.Encode(withNull), AuditAnchorRowEncoding.Encode(withNull));
        Assert.NotEqual(AuditAnchorRowEncoding.Encode(withNull), AuditAnchorRowEncoding.Encode(withEmpty));

        var signature = new SignatureRecord(
            Guid.NewGuid(), "Quality User", "batch.release", "Batch", "batch-1",
            "I approve release.", null, 1, new string('a', 64));
        Assert.Equal(AuditAnchorRowEncoding.Encode(signature), AuditAnchorRowEncoding.Encode(signature));
    }

    [Fact]
    public async Task Client_anchors_both_sqlite_streams_and_verifies_the_signed_receipt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"brmes-client-anchor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "recipes.db");
        var certificatePath = Path.Combine(directory, "receiver.pem");
        using var signingKey = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=AuditAnchorTests", signingKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        await File.WriteAllTextAsync(certificatePath, certificate.ExportCertificatePem());

        var dbServices = new ServiceCollection();
        dbServices.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        using var dbProvider = dbServices.BuildServiceProvider();
        await using (var scope = dbProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), "operator", "batch.start", "Batch", "batch-1", "started"));
            db.SignatureRecords.Add(new SignatureRecord(
                Guid.NewGuid(), "Quality User", "batch.release", "Batch", "batch-1",
                "I approve release.", null, 1, new string('b', 64)));
            await db.SaveChangesAsync();
        }

        var keyId = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        var handler = new SignedReceiptHandler(signingKey, keyId);
        var httpServices = new ServiceCollection();
        httpServices.AddHttpClient("AuditAnchor")
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var httpProvider = httpServices.BuildServiceProvider();
        var settings = new AuditAnchorSettings(
            true,
            new TimeOnly(2, 45),
            5,
            "d2a4b3b1-b8aa-49d3-b481-20af4818cb10",
            new Uri("https://anchor.test/"),
            new string('t', 40),
            certificatePath);
        using var client = new AuditAnchorHostedService(
            settings,
            dbProvider.GetRequiredService<IServiceScopeFactory>(),
            httpProvider.GetRequiredService<IHttpClientFactory>(),
            TimeProvider.System,
            NullLogger<AuditAnchorHostedService>.Instance);

        try
        {
            var receipt = await client.RunOnceAsync();

            Assert.Equal(1, handler.Manifest!.Streams.Single(x => x.Stream == AuditAnchorProtocol.AuditLogsStream).Count);
            Assert.Equal(1, handler.Manifest.Streams.Single(x => x.Stream == AuditAnchorProtocol.SignatureRecordsStream).Count);
            Assert.Equal(handler.Manifest.SnapshotId, receipt.SnapshotId);
            Assert.True(AuditAnchorProtocol.VerifyReceipt(receipt, signingKey, keyId));
            Assert.Null(client.Status().LastError);
            Assert.Equal(receipt.Sequence, client.Status().LastReceiptSequence);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Ledger_is_idempotent_per_snapshot_chains_receipts_and_rejects_mutation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"brmes-anchor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var signer = new TestSigner();
        try
        {
            var path = Path.Combine(directory, "anchor.db");
            var ledger = new AuditAnchorLedger(path, signer);
            await ledger.InitializeAsync();
            var firstManifest = CreateManifest();

            var first = await ledger.AcceptAsync(firstManifest);
            var repeated = await ledger.AcceptAsync(firstManifest);
            Assert.Equal(first.ReceiptHash, repeated.ReceiptHash);
            Assert.Equal(1, repeated.Sequence);
            Assert.True(AuditAnchorProtocol.VerifyReceipt(first, signer.PublicKey, signer.KeyId));

            var conflicting = firstManifest with
            {
                Streams = firstManifest.Streams.Select(x => x with { RootSha256 = new string('c', 64) }).ToArray()
            };
            await Assert.ThrowsAsync<AuditAnchorSnapshotConflictException>(
                () => ledger.AcceptAsync(conflicting));

            var secondManifest = CreateManifest(
                rootChar: 'b', snapshotId: Guid.Parse("e12eeac0-1ac1-4d59-86ee-b8c65e34bb2f"));
            var second = await ledger.AcceptAsync(secondManifest);
            Assert.Equal(2, second.Sequence);
            Assert.Equal(first.ReceiptHash, second.PreviousReceiptHash);

            var page = await ledger.ReadPageAsync(firstManifest.SourceId, 0, 20);
            Assert.Equal(2, page.LatestSequence);
            Assert.Equal(new long[] { 1, 2 }, page.Entries.Select(x => x.Receipt.Sequence));
            var firstPage = await ledger.ReadPageAsync(firstManifest.SourceId, 0, 1);
            var thirdManifest = CreateManifest(
                rootChar: 'd', snapshotId: Guid.Parse("bc4c3bef-cf12-4c25-909c-691cd7e5d5e9"));
            await ledger.AcceptAsync(thirdManifest);
            var frozenSecondPage = await ledger.ReadPageAsync(
                firstManifest.SourceId, 1, 1, firstPage.LatestSequence);
            Assert.Equal(firstPage.LatestSequence, frozenSecondPage.LatestSequence);
            Assert.Equal(2, frozenSecondPage.Entries.Single().Receipt.Sequence);

            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE anchor_receipts SET receipt_hash = 'tampered' WHERE sequence = 1";
            await Assert.ThrowsAsync<SqliteException>(() => update.ExecuteNonQueryAsync());
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM anchor_receipts WHERE sequence = 1";
            await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AuditMerkleAccumulator Tree(string stream, IEnumerable<string> rows)
    {
        var result = new AuditMerkleAccumulator(stream);
        foreach (var row in rows) result.Append(Encoding.UTF8.GetBytes(row));
        return result;
    }

    private static AuditAnchorManifest CreateManifest(char rootChar = 'a', Guid? snapshotId = null)
    {
        var hash = new string(rootChar, 64);
        return new AuditAnchorManifest(
            AuditAnchorProtocol.CurrentVersion,
            Guid.Parse("d2a4b3b1-b8aa-49d3-b481-20af4818cb10").ToString("D"),
            (snapshotId ?? Guid.Parse("438edb0c-a5fc-471e-9d94-6c31a1ea7719")).ToString("D"),
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            "1.0.0",
            "20261004150000_ImmutableSignatureRecords",
            [
                new AuditAnchorStreamRoot(AuditAnchorProtocol.SignatureRecordsStream, 1, hash),
                new AuditAnchorStreamRoot(AuditAnchorProtocol.AuditLogsStream, 2, hash)
            ]);
    }

    private static AuditAnchorReceiptPage[] CreateReceiptPages(
        RSA firstKey,
        RSA rotatedKey,
        string firstKeyId,
        string rotatedKeyId)
    {
        var entries = new List<AuditAnchorReceiptEntry>();
        string? previousHash = null;
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            var manifest = CreateManifest(
                (char)('a' + sequence),
                Guid.NewGuid());
            var keyId = sequence < 3 ? firstKeyId : rotatedKeyId;
            var key = sequence < 3 ? firstKey : rotatedKey;
            var receipt = AuditAnchorProtocol.SignReceipt(
                manifest.SourceId,
                sequence,
                manifest.SnapshotId,
                AuditAnchorProtocol.ManifestHash(manifest),
                previousHash,
                DateTimeOffset.UtcNow,
                keyId,
                key);
            entries.Add(new AuditAnchorReceiptEntry(manifest, receipt));
            previousHash = receipt.ReceiptHash;
        }

        return
        [
            new AuditAnchorReceiptPage(entries[0].Receipt.SourceId, 3, entries.Take(2).ToArray()),
            new AuditAnchorReceiptPage(entries[0].Receipt.SourceId, 3, entries.Skip(2).ToArray())
        ];
    }

    private sealed class TestSigner : IAuditAnchorReceiptSigner, IDisposable
    {
        private readonly RSA _key = RSA.Create(2048);
        public string KeyId => "test-key";
        public RSA PublicKey => _key;
        public byte[] Sign(ReadOnlySpan<byte> payload) =>
            _key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        public void Dispose() => _key.Dispose();
    }

    private sealed class SignedReceiptHandler(RSA signingKey, string keyId) : HttpMessageHandler
    {
        public AuditAnchorManifest? Manifest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Manifest = await request.Content!.ReadFromJsonAsync<AuditAnchorManifest>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken);
            Assert.NotNull(Manifest);
            var receipt = AuditAnchorProtocol.SignReceipt(
                Manifest!.SourceId,
                1,
                Manifest.SnapshotId,
                AuditAnchorProtocol.ManifestHash(Manifest),
                null,
                DateTimeOffset.UtcNow,
                keyId,
                signingKey);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(receipt, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
        }
    }
}
