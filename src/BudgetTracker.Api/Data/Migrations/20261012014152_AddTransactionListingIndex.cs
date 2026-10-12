using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BudgetTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionListingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_transactions_user_id_posted_on_id",
                table: "transactions",
                columns: new[] { "user_id", "posted_on", "id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_transactions_user_id_posted_on_id",
                table: "transactions");
        }
    }
}
