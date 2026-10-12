using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BudgetTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    match_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    pattern = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    mark_transfer = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rules", x => x.id);
                    table.CheckConstraint("ck_rules_has_action", "category_id IS NOT NULL OR mark_transfer");
                    table.CheckConstraint("ck_rules_match_type", "match_type IN ('Contains', 'StartsWith', 'Equals')");
                    table.CheckConstraint("ck_rules_pattern_not_blank", "pattern ~ '\\S'");
                    table.CheckConstraint("ck_rules_position_nonnegative", "position >= 0");
                    table.ForeignKey(
                        name: "fk_rules_categories_user_id_category_id",
                        columns: x => new { x.user_id, x.category_id },
                        principalTable: "categories",
                        principalColumns: new[] { "user_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_rules_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rules_user_id_category_id",
                table: "rules",
                columns: new[] { "user_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_rules_user_id_position_id",
                table: "rules",
                columns: new[] { "user_id", "position", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rules");
        }
    }
}
