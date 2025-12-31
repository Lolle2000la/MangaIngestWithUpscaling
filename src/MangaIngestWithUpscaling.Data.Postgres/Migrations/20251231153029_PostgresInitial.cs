using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MangaIngestWithUpscaling.Data.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class PostgresInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    NormalizedName = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    PreferredCulture = table.Column<string>(type: "text", nullable: true),
                    UserName = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    NormalizedUserName = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    Email = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    NormalizedEmail = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "PersistedTasks",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    Data = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    ProcessedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    Order = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersistedTasks", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "UpscalerProfiles",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    Name = table.Column<string>(type: "text", nullable: false),
                    UpscalerMethod = table.Column<string>(type: "text", nullable: false),
                    ScalingFactor = table.Column<string>(type: "text", nullable: false),
                    CompressionFormat = table.Column<string>(type: "text", nullable: false),
                    Quality = table.Column<int>(type: "integer", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    ModifiedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpscalerProfiles", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    RoleId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    Key = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Expiration = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_AspNetUserLogins",
                        x => new { x.LoginProvider, x.ProviderKey }
                    );
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    RoleId = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_AspNetUserTokens",
                        x => new
                        {
                            x.UserId,
                            x.LoginProvider,
                            x.Name,
                        }
                    );
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Libraries",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IngestPath = table.Column<string>(type: "text", nullable: false),
                    NotUpscaledLibraryPath = table.Column<string>(type: "text", nullable: false),
                    UpscaledLibraryPath = table.Column<string>(type: "text", nullable: true),
                    UpscaleOnIngest = table.Column<bool>(type: "boolean", nullable: false),
                    UpscalerProfileId = table.Column<int>(type: "integer", nullable: true),
                    MergeChapterParts = table.Column<bool>(type: "boolean", nullable: false),
                    StripDetectionMode = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    ModifiedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    KavitaConfig_NotUpscaledMountPoint = table.Column<string>(
                        type: "text",
                        nullable: true
                    ),
                    KavitaConfig_UpscaledMountPoint = table.Column<string>(
                        type: "text",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Libraries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Libraries_UpscalerProfiles_UpscalerProfileId",
                        column: x => x.UpscalerProfileId,
                        principalTable: "UpscalerProfiles",
                        principalColumn: "Id"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "FilteredImages",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    OriginalFileName = table.Column<string>(type: "text", nullable: false),
                    ThumbnailBase64 = table.Column<string>(type: "text", nullable: true),
                    MimeType = table.Column<string>(type: "text", nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    DateAdded = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ContentHash = table.Column<string>(type: "text", nullable: true),
                    PerceptualHash = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    LastMatchedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilteredImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FilteredImages_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "LibraryFilterRules",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    Pattern = table.Column<string>(type: "text", nullable: false),
                    PatternType = table.Column<string>(type: "text", nullable: false),
                    TargetField = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryFilterRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryFilterRules_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "LibraryRenameRules",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    Pattern = table.Column<string>(type: "text", nullable: false),
                    PatternType = table.Column<string>(type: "text", nullable: false),
                    TargetField = table.Column<string>(type: "text", nullable: false),
                    Replacement = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryRenameRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryRenameRules_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "MangaSeries",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    PrimaryTitle = table.Column<string>(type: "text", nullable: false),
                    Author = table.Column<string>(type: "text", nullable: true),
                    ShouldUpscale = table.Column<bool>(type: "boolean", nullable: true),
                    MergeChapterParts = table.Column<bool>(type: "boolean", nullable: true),
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    UpscalerProfilePreferenceId = table.Column<int>(
                        type: "integer",
                        nullable: true
                    ),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    ModifiedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MangaSeries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MangaSeries_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_MangaSeries_UpscalerProfiles_UpscalerProfilePreferenceId",
                        column: x => x.UpscalerProfilePreferenceId,
                        principalTable: "UpscalerProfiles",
                        principalColumn: "Id"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Chapters",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    MangaId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    RelativePath = table.Column<string>(type: "text", nullable: false),
                    IsUpscaled = table.Column<bool>(type: "boolean", nullable: false),
                    UpscalerProfileId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    ModifiedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chapters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Chapters_MangaSeries_MangaId",
                        column: x => x.MangaId,
                        principalTable: "MangaSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_Chapters_UpscalerProfiles_UpscalerProfileId",
                        column: x => x.UpscalerProfileId,
                        principalTable: "UpscalerProfiles",
                        principalColumn: "Id"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "MangaAlternativeTitles",
                columns: table => new
                {
                    Title = table.Column<string>(type: "text", nullable: false),
                    MangaId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MangaAlternativeTitles", x => new { x.MangaId, x.Title });
                    table.ForeignKey(
                        name: "FK_MangaAlternativeTitles_MangaSeries_MangaId",
                        column: x => x.MangaId,
                        principalTable: "MangaSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "ChapterSplitProcessingStates",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    ChapterId = table.Column<int>(type: "integer", nullable: false),
                    LastProcessedDetectorVersion = table.Column<int>(
                        type: "integer",
                        nullable: false
                    ),
                    LastAppliedDetectorVersion = table.Column<int>(
                        type: "integer",
                        nullable: false
                    ),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ModifiedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChapterSplitProcessingStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChapterSplitProcessingStates_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "MergedChapterInfos",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    ChapterId = table.Column<int>(type: "integer", nullable: false),
                    OriginalParts = table.Column<string>(type: "jsonb", nullable: false),
                    MergedChapterNumber = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MergedChapterInfos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MergedChapterInfos_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "StripSplitFindings",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    ChapterId = table.Column<int>(type: "integer", nullable: false),
                    PageFileName = table.Column<string>(type: "text", nullable: false),
                    SplitJson = table.Column<string>(type: "text", nullable: false),
                    DetectorVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripSplitFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StripSplitFindings_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_Key",
                table: "ApiKeys",
                column: "Key",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_UserId",
                table: "ApiKeys",
                column: "UserId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId"
            );

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId"
            );

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail"
            );

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_IsUpscaled",
                table: "Chapters",
                column: "IsUpscaled"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_MangaId",
                table: "Chapters",
                column: "MangaId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_RelativePath_MangaId",
                table: "Chapters",
                columns: new[] { "RelativePath", "MangaId" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_UpscalerProfileId",
                table: "Chapters",
                column: "UpscalerProfileId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_ChapterSplitProcessingStates_ChapterId",
                table: "ChapterSplitProcessingStates",
                column: "ChapterId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_FilteredImages_ContentHash",
                table: "FilteredImages",
                column: "ContentHash"
            );

            migrationBuilder.CreateIndex(
                name: "IX_FilteredImages_DateAdded",
                table: "FilteredImages",
                column: "DateAdded"
            );

            migrationBuilder.CreateIndex(
                name: "IX_FilteredImages_LibraryId",
                table: "FilteredImages",
                column: "LibraryId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_FilteredImages_LibraryId_OriginalFileName",
                table: "FilteredImages",
                columns: new[] { "LibraryId", "OriginalFileName" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_FilteredImages_OccurrenceCount",
                table: "FilteredImages",
                column: "OccurrenceCount"
            );

            migrationBuilder.CreateIndex(
                name: "IX_FilteredImages_PerceptualHash",
                table: "FilteredImages",
                column: "PerceptualHash"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Libraries_UpscalerProfileId",
                table: "Libraries",
                column: "UpscalerProfileId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_LibraryFilterRules_LibraryId",
                table: "LibraryFilterRules",
                column: "LibraryId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_LibraryRenameRules_LibraryId",
                table: "LibraryRenameRules",
                column: "LibraryId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_MangaAlternativeTitles_Title",
                table: "MangaAlternativeTitles",
                column: "Title",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_MangaSeries_LibraryId",
                table: "MangaSeries",
                column: "LibraryId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_MangaSeries_PrimaryTitle",
                table: "MangaSeries",
                column: "PrimaryTitle",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_MangaSeries_UpscalerProfilePreferenceId",
                table: "MangaSeries",
                column: "UpscalerProfilePreferenceId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_MergedChapterInfos_ChapterId",
                table: "MergedChapterInfos",
                column: "ChapterId",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_MergedChapterInfos_MergedChapterNumber",
                table: "MergedChapterInfos",
                column: "MergedChapterNumber"
            );

            migrationBuilder.CreateIndex(
                name: "IX_PersistedTasks_CreatedAt",
                table: "PersistedTasks",
                column: "CreatedAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_PersistedTasks_Order",
                table: "PersistedTasks",
                column: "Order"
            );

            migrationBuilder.CreateIndex(
                name: "IX_PersistedTasks_ProcessedAt",
                table: "PersistedTasks",
                column: "ProcessedAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_PersistedTasks_Status",
                table: "PersistedTasks",
                column: "Status"
            );

            migrationBuilder.CreateIndex(
                name: "IX_PersistedTasks_Status_CreatedAt",
                table: "PersistedTasks",
                columns: new[] { "Status", "CreatedAt" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_StripSplitFindings_ChapterId",
                table: "StripSplitFindings",
                column: "ChapterId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_UpscalerProfiles_Deleted",
                table: "UpscalerProfiles",
                column: "Deleted"
            );

            migrationBuilder.CreateIndex(
                name: "IX_UpscalerProfiles_Id_Deleted",
                table: "UpscalerProfiles",
                columns: new[] { "Id", "Deleted" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ApiKeys");

            migrationBuilder.DropTable(name: "AspNetRoleClaims");

            migrationBuilder.DropTable(name: "AspNetUserClaims");

            migrationBuilder.DropTable(name: "AspNetUserLogins");

            migrationBuilder.DropTable(name: "AspNetUserRoles");

            migrationBuilder.DropTable(name: "AspNetUserTokens");

            migrationBuilder.DropTable(name: "ChapterSplitProcessingStates");

            migrationBuilder.DropTable(name: "DataProtectionKeys");

            migrationBuilder.DropTable(name: "FilteredImages");

            migrationBuilder.DropTable(name: "LibraryFilterRules");

            migrationBuilder.DropTable(name: "LibraryRenameRules");

            migrationBuilder.DropTable(name: "MangaAlternativeTitles");

            migrationBuilder.DropTable(name: "MergedChapterInfos");

            migrationBuilder.DropTable(name: "PersistedTasks");

            migrationBuilder.DropTable(name: "StripSplitFindings");

            migrationBuilder.DropTable(name: "AspNetRoles");

            migrationBuilder.DropTable(name: "AspNetUsers");

            migrationBuilder.DropTable(name: "Chapters");

            migrationBuilder.DropTable(name: "MangaSeries");

            migrationBuilder.DropTable(name: "Libraries");

            migrationBuilder.DropTable(name: "UpscalerProfiles");
        }
    }
}
