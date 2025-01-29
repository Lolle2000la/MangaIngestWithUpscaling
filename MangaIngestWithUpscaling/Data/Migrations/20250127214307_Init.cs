using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations;

/// <inheritdoc />
public partial class Init : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AspNetRoles",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetRoles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUsers",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                LockoutEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUsers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Libraries",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                IngestPath = table.Column<string>(type: "TEXT", nullable: false),
                NotUpscaledLibraryPath = table.Column<string>(type: "TEXT", nullable: false),
                UpscaledLibraryPath = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Libraries", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PersistedTasks",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Data = table.Column<string>(type: "jsonb", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PersistedTasks", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "UpscalerConfigs",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                UpscalerMethod = table.Column<string>(type: "TEXT", nullable: false),
                ScalingFactor = table.Column<string>(type: "TEXT", nullable: false),
                CompressionFormat = table.Column<string>(type: "TEXT", nullable: false),
                Quality = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UpscalerConfigs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AspNetRoleClaims",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RoleId = table.Column<string>(type: "TEXT", nullable: false),
                ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                table.ForeignKey(
                    name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "AspNetRoles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserClaims",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                table.ForeignKey(
                    name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserLogins",
            columns: table => new
            {
                LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                UserId = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                table.ForeignKey(
                    name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserRoles",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                RoleId = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                table.ForeignKey(
                    name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "AspNetRoles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserTokens",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                table.ForeignKey(
                    name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "LibraryFilterRules",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                Pattern = table.Column<string>(type: "TEXT", nullable: false),
                PatternType = table.Column<string>(type: "TEXT", nullable: false),
                TargetField = table.Column<string>(type: "TEXT", nullable: false),
                Action = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LibraryFilterRules", x => x.Id);
                table.ForeignKey(
                    name: "FK_LibraryFilterRules_Libraries_LibraryId",
                    column: x => x.LibraryId,
                    principalTable: "Libraries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MangaSeries",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                PrimaryTitle = table.Column<string>(type: "TEXT", nullable: false),
                Author = table.Column<string>(type: "TEXT", nullable: false),
                LibraryId = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MangaSeries", x => x.Id);
                table.ForeignKey(
                    name: "FK_MangaSeries_Libraries_LibraryId",
                    column: x => x.LibraryId,
                    principalTable: "Libraries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Chapters",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                MangaId = table.Column<int>(type: "INTEGER", nullable: false),
                FileName = table.Column<string>(type: "TEXT", nullable: false),
                RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                IsUpscaled = table.Column<bool>(type: "INTEGER", nullable: false),
                UpscalerConfigId = table.Column<int>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Chapters", x => x.Id);
                table.ForeignKey(
                    name: "FK_Chapters_MangaSeries_MangaId",
                    column: x => x.MangaId,
                    principalTable: "MangaSeries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_Chapters_UpscalerConfigs_UpscalerConfigId",
                    column: x => x.UpscalerConfigId,
                    principalTable: "UpscalerConfigs",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "MangaAlternativeTitles",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Title = table.Column<string>(type: "TEXT", nullable: false),
                MangaId = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MangaAlternativeTitles", x => x.Id);
                table.ForeignKey(
                    name: "FK_MangaAlternativeTitles_MangaSeries_MangaId",
                    column: x => x.MangaId,
                    principalTable: "MangaSeries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "UpscalingQueueEntries",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                UpscalerConfigId = table.Column<int>(type: "INTEGER", nullable: false),
                QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UpscalingQueueEntries", x => x.Id);
                table.ForeignKey(
                    name: "FK_UpscalingQueueEntries_Chapters_ChapterId",
                    column: x => x.ChapterId,
                    principalTable: "Chapters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_UpscalingQueueEntries_UpscalerConfigs_UpscalerConfigId",
                    column: x => x.UpscalerConfigId,
                    principalTable: "UpscalerConfigs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AspNetRoleClaims_RoleId",
            table: "AspNetRoleClaims",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "RoleNameIndex",
            table: "AspNetRoles",
            column: "NormalizedName",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserClaims_UserId",
            table: "AspNetUserClaims",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserLogins_UserId",
            table: "AspNetUserLogins",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserRoles_RoleId",
            table: "AspNetUserRoles",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "EmailIndex",
            table: "AspNetUsers",
            column: "NormalizedEmail");

        migrationBuilder.CreateIndex(
            name: "UserNameIndex",
            table: "AspNetUsers",
            column: "NormalizedUserName",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Chapters_MangaId",
            table: "Chapters",
            column: "MangaId");

        migrationBuilder.CreateIndex(
            name: "IX_Chapters_UpscalerConfigId",
            table: "Chapters",
            column: "UpscalerConfigId");

        migrationBuilder.CreateIndex(
            name: "IX_LibraryFilterRules_LibraryId",
            table: "LibraryFilterRules",
            column: "LibraryId");

        migrationBuilder.CreateIndex(
            name: "IX_MangaAlternativeTitles_MangaId",
            table: "MangaAlternativeTitles",
            column: "MangaId");

        migrationBuilder.CreateIndex(
            name: "IX_MangaSeries_LibraryId",
            table: "MangaSeries",
            column: "LibraryId");

        migrationBuilder.CreateIndex(
            name: "IX_PersistedTasks_CreatedAt",
            table: "PersistedTasks",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_PersistedTasks_ProcessedAt",
            table: "PersistedTasks",
            column: "ProcessedAt");

        migrationBuilder.CreateIndex(
            name: "IX_PersistedTasks_Status",
            table: "PersistedTasks",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_UpscalingQueueEntries_ChapterId",
            table: "UpscalingQueueEntries",
            column: "ChapterId");

        migrationBuilder.CreateIndex(
            name: "IX_UpscalingQueueEntries_UpscalerConfigId",
            table: "UpscalingQueueEntries",
            column: "UpscalerConfigId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AspNetRoleClaims");

        migrationBuilder.DropTable(
            name: "AspNetUserClaims");

        migrationBuilder.DropTable(
            name: "AspNetUserLogins");

        migrationBuilder.DropTable(
            name: "AspNetUserRoles");

        migrationBuilder.DropTable(
            name: "AspNetUserTokens");

        migrationBuilder.DropTable(
            name: "LibraryFilterRules");

        migrationBuilder.DropTable(
            name: "MangaAlternativeTitles");

        migrationBuilder.DropTable(
            name: "PersistedTasks");

        migrationBuilder.DropTable(
            name: "UpscalingQueueEntries");

        migrationBuilder.DropTable(
            name: "AspNetRoles");

        migrationBuilder.DropTable(
            name: "AspNetUsers");

        migrationBuilder.DropTable(
            name: "Chapters");

        migrationBuilder.DropTable(
            name: "MangaSeries");

        migrationBuilder.DropTable(
            name: "UpscalerConfigs");

        migrationBuilder.DropTable(
            name: "Libraries");
    }
}
