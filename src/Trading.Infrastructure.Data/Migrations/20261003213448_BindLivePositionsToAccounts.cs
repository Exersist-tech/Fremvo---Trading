using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BindLivePositionsToAccounts : Migration
    {
        private static readonly string[] s_ownerAccountSymbolColumns =
            ["UserId", "Mode", "ExchangeAccountId", "Symbol"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.AddColumn<Guid>(
                name: "ExchangeAccountId",
                table: "Positions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Positions_UserId_Mode_ExchangeAccountId_Symbol",
                table: "Positions",
                columns: s_ownerAccountSymbolColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropIndex(
                name: "IX_Positions_UserId_Mode_ExchangeAccountId_Symbol",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "ExchangeAccountId",
                table: "Positions");
        }
    }
}
