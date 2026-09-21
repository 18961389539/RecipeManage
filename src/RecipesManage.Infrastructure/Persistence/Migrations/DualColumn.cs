using Microsoft.EntityFrameworkCore.Migrations;

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// SQLite 与 PostgreSQL 共用同一条 Initial 迁移：SQLite 保持历史 TEXT/INTEGER，
/// Npgsql 发出 uuid / boolean / timestamptz / jsonb，避免活库上的类型错配。
/// </summary>
internal static class DualColumn
{
    public readonly record struct Typeset(
        string Json,
        string Guid,
        string Text,
        string Int,
        string Bool,
        string Real,
        string Ts);

    public static bool IsNpgsql(MigrationBuilder builder) =>
        builder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    public static Typeset For(MigrationBuilder builder) =>
        IsNpgsql(builder)
            ? new(
                Json: "jsonb",
                Guid: "uuid",
                Text: "text",
                Int: "integer",
                Bool: "boolean",
                Real: "double precision",
                Ts: "timestamp with time zone")
            : new(
                Json: "TEXT",
                Guid: "TEXT",
                Text: "TEXT",
                Int: "INTEGER",
                Bool: "INTEGER",
                Real: "REAL",
                Ts: "TEXT");
}
