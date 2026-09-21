using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var t = DualColumn.For(migrationBuilder);
            var json = t.Json;
            var guid = t.Guid;
            var text = t.Text;
            var integer = t.Int;
            var boolean = t.Bool;
            var real = t.Real;
            var ts = t.Ts;

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    UserId = table.Column<Guid>(type: guid, nullable: true),
                    UserName = table.Column<string>(type: text, nullable: false),
                    Action = table.Column<string>(type: text, nullable: false),
                    EntityType = table.Column<string>(type: text, nullable: false),
                    EntityId = table.Column<string>(type: text, nullable: false),
                    Detail = table.Column<string>(type: text, nullable: true),
                    At = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "equipment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    Code = table.Column<string>(type: text, nullable: false),
                    Name = table.Column<string>(type: text, nullable: false),
                    Protocol = table.Column<int>(type: integer, nullable: false),
                    Host = table.Column<string>(type: text, nullable: false),
                    Port = table.Column<int>(type: integer, nullable: false),
                    PlcModel = table.Column<string>(type: text, nullable: false),
                    Rack = table.Column<int>(type: integer, nullable: false),
                    Slot = table.Column<int>(type: integer, nullable: false),
                    Enabled = table.Column<bool>(type: boolean, nullable: false),
                    TagMapJson = table.Column<string>(type: json, nullable: false),
                    WatchdogJson = table.Column<string>(type: json, nullable: true),
                    Description = table.Column<string>(type: text, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "handshake_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    BatchId = table.Column<Guid>(type: guid, nullable: false),
                    StepId = table.Column<Guid>(type: guid, nullable: true),
                    StepCode = table.Column<string>(type: text, nullable: false),
                    Phase = table.Column<string>(type: text, nullable: false),
                    Kind = table.Column<string>(type: text, nullable: false),
                    Detail = table.Column<string>(type: text, nullable: true),
                    RemainingSeconds = table.Column<double>(type: real, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_handshake_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "master_recipes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    Code = table.Column<string>(type: text, nullable: false),
                    Name = table.Column<string>(type: text, nullable: false),
                    ProductCode = table.Column<string>(type: text, nullable: false),
                    ProductName = table.Column<string>(type: text, nullable: false),
                    Description = table.Column<string>(type: text, nullable: true),
                    Lifecycle = table.Column<int>(type: integer, nullable: false),
                    CurrentDraftVersionId = table.Column<Guid>(type: guid, nullable: true),
                    CurrentApprovedVersionId = table.Column<Guid>(type: guid, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_master_recipes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "process_alarms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    BatchId = table.Column<Guid>(type: guid, nullable: false),
                    BatchNo = table.Column<string>(type: text, nullable: false),
                    StepId = table.Column<Guid>(type: guid, nullable: true),
                    StepCode = table.Column<string>(type: text, nullable: false),
                    Code = table.Column<string>(type: text, nullable: false),
                    Severity = table.Column<string>(type: text, nullable: false),
                    Message = table.Column<string>(type: text, nullable: false),
                    RaisedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: ts, nullable: true),
                    AcknowledgedBy = table.Column<string>(type: text, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_process_alarms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "production_batches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    BatchNo = table.Column<string>(type: text, nullable: false),
                    MasterRecipeId = table.Column<Guid>(type: guid, nullable: false),
                    RecipeVersionId = table.Column<Guid>(type: guid, nullable: false),
                    EquipmentId = table.Column<Guid>(type: guid, nullable: false),
                    ProductCode = table.Column<string>(type: text, nullable: false),
                    ProductName = table.Column<string>(type: text, nullable: false),
                    Status = table.Column<int>(type: integer, nullable: false),
                    ControlRecipeJson = table.Column<string>(type: json, nullable: false),
                    CurrentStepId = table.Column<Guid>(type: guid, nullable: true),
                    CurrentStepIndex = table.Column<int>(type: integer, nullable: false),
                    HandshakePhase = table.Column<string>(type: text, nullable: false),
                    FaultCode = table.Column<string>(type: text, nullable: true),
                    FaultMessage = table.Column<string>(type: text, nullable: true),
                    CreatedBy = table.Column<Guid>(type: guid, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: ts, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: ts, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_batches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    UserName = table.Column<string>(type: text, maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: text, maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: text, nullable: false),
                    Role = table.Column<int>(type: integer, nullable: false),
                    IsActive = table.Column<bool>(type: boolean, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "recipe_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    MasterRecipeId = table.Column<Guid>(type: guid, nullable: false),
                    VersionNumber = table.Column<int>(type: integer, nullable: false),
                    Status = table.Column<int>(type: integer, nullable: false),
                    ChangeNote = table.Column<string>(type: text, nullable: true),
                    CreatedBy = table.Column<Guid>(type: guid, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: ts, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: ts, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipe_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recipe_versions_master_recipes_MasterRecipeId",
                        column: x => x.MasterRecipeId,
                        principalTable: "master_recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "batch_step_executions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    BatchId = table.Column<Guid>(type: guid, nullable: false),
                    StepId = table.Column<Guid>(type: guid, nullable: false),
                    StepCode = table.Column<string>(type: text, nullable: false),
                    StepName = table.Column<string>(type: text, nullable: false),
                    StepType = table.Column<int>(type: integer, nullable: false),
                    Ordinal = table.Column<int>(type: integer, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: ts, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: ts, nullable: true),
                    Outcome = table.Column<string>(type: text, nullable: false),
                    QualityJson = table.Column<string>(type: text, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_batch_step_executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_batch_step_executions_production_batches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "production_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "process_samples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    BatchId = table.Column<Guid>(type: guid, nullable: false),
                    StepId = table.Column<Guid>(type: guid, nullable: true),
                    SampledAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    Tag = table.Column<string>(type: text, nullable: false),
                    Value = table.Column<double>(type: real, nullable: false),
                    Unit = table.Column<string>(type: text, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_process_samples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_process_samples_production_batches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "production_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "approval_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    RecipeVersionId = table.Column<Guid>(type: guid, nullable: false),
                    Level = table.Column<int>(type: integer, nullable: false),
                    Decision = table.Column<int>(type: integer, nullable: false),
                    ReviewerId = table.Column<Guid>(type: guid, nullable: true),
                    ReviewerName = table.Column<string>(type: text, nullable: true),
                    Comment = table.Column<string>(type: text, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: ts, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_approval_records_recipe_versions_RecipeVersionId",
                        column: x => x.RecipeVersionId,
                        principalTable: "recipe_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recipe_edges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    RecipeVersionId = table.Column<Guid>(type: guid, nullable: false),
                    FromStepId = table.Column<Guid>(type: guid, nullable: false),
                    ToStepId = table.Column<Guid>(type: guid, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipe_edges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recipe_edges_recipe_versions_RecipeVersionId",
                        column: x => x.RecipeVersionId,
                        principalTable: "recipe_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recipe_steps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    RecipeVersionId = table.Column<Guid>(type: guid, nullable: false),
                    Code = table.Column<string>(type: text, nullable: false),
                    Name = table.Column<string>(type: text, nullable: false),
                    Type = table.Column<int>(type: integer, nullable: false),
                    Ordinal = table.Column<int>(type: integer, nullable: false),
                    CanvasX = table.Column<double>(type: real, nullable: false),
                    CanvasY = table.Column<double>(type: real, nullable: false),
                    WatchdogSeconds = table.Column<int>(type: integer, nullable: false),
                    Description = table.Column<string>(type: text, nullable: true),
                    UnitProcedure = table.Column<string>(type: text, nullable: true),
                    Operation = table.Column<string>(type: text, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipe_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recipe_steps_recipe_versions_RecipeVersionId",
                        column: x => x.RecipeVersionId,
                        principalTable: "recipe_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recipe_parameters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    RecipeStepId = table.Column<Guid>(type: guid, nullable: false),
                    SlotIndex = table.Column<int>(type: integer, nullable: false),
                    Name = table.Column<string>(type: text, nullable: false),
                    EngineeringUnit = table.Column<string>(type: text, nullable: false),
                    Setpoint = table.Column<double>(type: real, nullable: false),
                    Min = table.Column<double>(type: real, nullable: true),
                    Max = table.Column<double>(type: real, nullable: true),
                    WriteToPlc = table.Column<bool>(type: boolean, nullable: false),
                    ArchiveAsQuality = table.Column<bool>(type: boolean, nullable: false),
                    ScaleWithBatch = table.Column<bool>(type: boolean, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: ts, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: ts, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipe_parameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recipe_parameters_recipe_steps_RecipeStepId",
                        column: x => x.RecipeStepId,
                        principalTable: "recipe_steps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_approval_records_RecipeVersionId",
                table: "approval_records",
                column: "RecipeVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_At",
                table: "audit_logs",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_batch_step_executions_BatchId",
                table: "batch_step_executions",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_equipment_Code",
                table: "equipment",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_handshake_events_BatchId",
                table: "handshake_events",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_master_recipes_Code",
                table: "master_recipes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_process_alarms_BatchId",
                table: "process_alarms",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_process_alarms_RaisedAt",
                table: "process_alarms",
                column: "RaisedAt");

            migrationBuilder.CreateIndex(
                name: "IX_process_samples_BatchId_SampledAt",
                table: "process_samples",
                columns: new[] { "BatchId", "SampledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_production_batches_BatchNo",
                table: "production_batches",
                column: "BatchNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recipe_edges_RecipeVersionId",
                table: "recipe_edges",
                column: "RecipeVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_recipe_parameters_RecipeStepId",
                table: "recipe_parameters",
                column: "RecipeStepId");

            migrationBuilder.CreateIndex(
                name: "IX_recipe_steps_RecipeVersionId",
                table: "recipe_steps",
                column: "RecipeVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_recipe_versions_MasterRecipeId_VersionNumber",
                table: "recipe_versions",
                columns: new[] { "MasterRecipeId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_UserName",
                table: "users",
                column: "UserName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approval_records");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "batch_step_executions");

            migrationBuilder.DropTable(
                name: "equipment");

            migrationBuilder.DropTable(
                name: "handshake_events");

            migrationBuilder.DropTable(
                name: "process_alarms");

            migrationBuilder.DropTable(
                name: "process_samples");

            migrationBuilder.DropTable(
                name: "recipe_edges");

            migrationBuilder.DropTable(
                name: "recipe_parameters");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "production_batches");

            migrationBuilder.DropTable(
                name: "recipe_steps");

            migrationBuilder.DropTable(
                name: "recipe_versions");

            migrationBuilder.DropTable(
                name: "master_recipes");
        }
    }
}
