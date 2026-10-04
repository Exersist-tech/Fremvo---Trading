using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class HashInvitationCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.Sql("""
                UPDATE [Invitations]
                SET [IsActive] = 0
                WHERE [Code] COLLATE Latin1_General_100_BIN2 LIKE '%[^A-Za-z0-9-]%';

                UPDATE [Invitations]
                SET [Code] = CONVERT(varchar(64),
                    HASHBYTES('SHA2_256', CONVERT(varchar(64),
                        UPPER([Code] COLLATE Latin1_General_100_CI_AS))), 2);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Invitation bearer codes cannot be recovered from their digests.");
        }
    }
}
