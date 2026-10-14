using System.Buffers.Binary;
using System.Text;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Infrastructure.Persistence;

public static class AuditAnchorRowEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(AuditLog row)
    {
        using var buffer = new MemoryStream();
        WriteNullableGuid(buffer, row.UserId);
        WriteString(buffer, row.UserName);
        WriteString(buffer, row.Action);
        WriteString(buffer, row.EntityType);
        WriteString(buffer, row.EntityId);
        WriteNullableString(buffer, row.Detail);
        WriteTimestamp(buffer, row.At);
        WriteGuid(buffer, row.Id);
        WriteTimestamp(buffer, row.CreatedAt);
        WriteNullableTimestamp(buffer, row.UpdatedAt);
        return buffer.ToArray();
    }

    public static byte[] Encode(SignatureRecord row)
    {
        using var buffer = new MemoryStream();
        WriteNullableGuid(buffer, row.UserId);
        WriteString(buffer, row.SignerName);
        WriteString(buffer, row.Action);
        WriteString(buffer, row.EntityType);
        WriteString(buffer, row.EntityId);
        WriteString(buffer, row.Meaning);
        WriteNullableString(buffer, row.Detail);
        WriteNullableInt32(buffer, row.ContentHashVersion);
        WriteNullableString(buffer, row.ContentHash);
        WriteTimestamp(buffer, row.SignedAt);
        WriteGuid(buffer, row.Id);
        WriteTimestamp(buffer, row.CreatedAt);
        WriteNullableTimestamp(buffer, row.UpdatedAt);
        return buffer.ToArray();
    }

    private static void WriteGuid(Stream stream, Guid value) =>
        WriteBytes(stream, Encoding.ASCII.GetBytes(value.ToString("N").ToLowerInvariant()));

    private static void WriteNullableGuid(Stream stream, Guid? value)
    {
        stream.WriteByte(value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue) WriteGuid(stream, value.Value);
    }

    private static void WriteString(Stream stream, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteBytes(stream, StrictUtf8.GetBytes(value));
    }

    private static void WriteNullableString(Stream stream, string? value)
    {
        stream.WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is not null) WriteBytes(stream, StrictUtf8.GetBytes(value));
    }

    private static void WriteBytes(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    private static void WriteNullableInt32(Stream stream, int? value)
    {
        stream.WriteByte(value.HasValue ? (byte)1 : (byte)0);
        if (!value.HasValue) return;
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value.Value);
        stream.Write(bytes);
    }

    private static void WriteTimestamp(Stream stream, DateTimeOffset value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value.UtcDateTime.Ticks);
        stream.Write(bytes);
    }

    private static void WriteNullableTimestamp(Stream stream, DateTimeOffset? value)
    {
        stream.WriteByte(value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue) WriteTimestamp(stream, value.Value);
    }
}
