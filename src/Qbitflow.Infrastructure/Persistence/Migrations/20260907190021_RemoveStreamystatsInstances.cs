using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qbitflow.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Drops any instance left over from the removed Streamystats source type.
    ///
    /// Instance.SourceType is persisted as a string, so once the enum value is gone a surviving
    /// row can no longer be materialized -- the Instances page and every rule run would throw on
    /// it, and the row could not be deleted through the UI because that page is what breaks.
    /// Raw SQL rather than the DbSet for exactly that reason: it never goes through the enum
    /// conversion.
    /// </summary>
    public partial class RemoveStreamystatsInstances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM Instances WHERE SourceType = 'Streamystats';");
        }

        /// <summary>
        /// Deliberately empty: the deleted instance's credentials were encrypted and are gone with
        /// it, so there is nothing meaningful to restore.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
