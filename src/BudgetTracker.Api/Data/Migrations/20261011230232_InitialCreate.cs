using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BudgetTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_email_lowercase", "email = lower(email)");
                });

            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    invert_sign = table.Column<bool>(type: "boolean", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.UniqueConstraint("ak_accounts_user_id_id", x => new { x.user_id, x.id });
                    table.CheckConstraint("ck_accounts_type", "type IN ('Checking', 'Savings', 'Credit')");
                    table.ForeignKey(
                        name: "fk_accounts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.UniqueConstraint("ak_categories_user_id_id", x => new { x.user_id, x.id });
                    table.ForeignKey(
                        name: "fk_categories_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "budgets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_budgets", x => x.id);
                    table.CheckConstraint("ck_budgets_amount_nonnegative", "amount >= 0");
                    table.CheckConstraint("ck_budgets_month_first_day", "EXTRACT(DAY FROM month) = 1");
                    table.ForeignKey(
                        name: "fk_budgets_categories_user_id_category_id",
                        columns: x => new { x.user_id, x.category_id },
                        principalTable: "categories",
                        principalColumns: new[] { "user_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_budgets_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    posted_on = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_transfer = table.Column<bool>(type: "boolean", nullable: false),
                    import_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    import_seq = table.Column<short>(type: "smallint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transactions", x => x.id);
                    table.CheckConstraint("ck_transactions_amount_nonzero", "amount <> 0");
                    table.CheckConstraint("ck_transactions_import_pair", "(import_hash IS NULL) = (import_seq IS NULL)");
                    table.ForeignKey(
                        name: "fk_transactions_accounts_user_id_account_id",
                        columns: x => new { x.user_id, x.account_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "user_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_transactions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Hand-written: EF can only emit ON DELETE SET NULL for every column of a
            // composite key, which would null the NOT NULL user_id. The column list
            // (PostgreSQL 15+) nulls only category_id. The model uses NoAction for
            // this relationship to match; BudgetDbContext tests cover the behavior.
            migrationBuilder.Sql(
                """
                ALTER TABLE transactions
                    ADD CONSTRAINT fk_transactions_categories_user_id_category_id
                    FOREIGN KEY (user_id, category_id)
                    REFERENCES categories (user_id, id)
                    ON DELETE SET NULL (category_id);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_accounts_user_id_name",
                table: "accounts",
                columns: new[] { "user_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_budgets_user_id_category_id_month",
                table: "budgets",
                columns: new[] { "user_id", "category_id", "month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_categories_user_id_name",
                table: "categories",
                columns: new[] { "user_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_account_id_import_hash_import_seq",
                table: "transactions",
                columns: new[] { "account_id", "import_hash", "import_seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_account_id_posted_on",
                table: "transactions",
                columns: new[] { "account_id", "posted_on" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_transactions_user_id_account_id",
                table: "transactions",
                columns: new[] { "user_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transactions_user_id_category_id",
                table: "transactions",
                columns: new[] { "user_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budgets");

            migrationBuilder.DropTable(
                name: "transactions");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
