using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// Defense in depth: signed rows can be appended, but the application database rejects edits/removals.
/// A database owner can still remove these triggers; this is not an external trust anchor.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261004150000_ImmutableSignatureRecords")]
public sealed class ImmutableSignatureRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TRIGGER "TR_signature_records_immutable_update"
            BEFORE UPDATE ON "signature_records"
            BEGIN
                SELECT RAISE(ABORT, 'signature_records are immutable');
            END;
            """);

        migrationBuilder.Sql(
            """
            CREATE TRIGGER "TR_signature_records_immutable_delete"
            BEFORE DELETE ON "signature_records"
            BEGIN
                SELECT RAISE(ABORT, 'signature_records are immutable');
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "TR_signature_records_immutable_delete";""");
        migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "TR_signature_records_immutable_update";""");
    }
}
