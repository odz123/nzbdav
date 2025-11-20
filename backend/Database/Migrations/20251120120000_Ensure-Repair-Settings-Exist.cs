using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class EnsureRepairSettingsExist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Use INSERT OR IGNORE to avoid errors if settings already exist
            migrationBuilder.Sql(
                """
                INSERT OR IGNORE INTO ConfigItems (ConfigName, ConfigValue)
                VALUES
                    ('repair.sampling-rate', '0.15'),
                    ('repair.min-segments', '10'),
                    ('repair.adaptive-sampling', 'true'),
                    ('repair.cache-enabled', 'true'),
                    ('repair.cache-ttl-hours', '24'),
                    ('repair.parallel-files', '3');
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally left blank
        }
    }
}
