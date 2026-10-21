// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MKITS.SupportSuite.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ConsentPolicy = table.Column<int>(type: "INTEGER", nullable: false),
                    RequireEnrollmentApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OsName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    OsVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AgentVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    PublicKey = table.Column<byte[]>(type: "BLOB", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    EnrolledAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_DeviceGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "DeviceGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EnrollmentTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxUses = table.Column<int>(type: "INTEGER", nullable: false),
                    Uses = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrollmentTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnrollmentTokens_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EnrollmentTokens_DeviceGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "DeviceGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupAccess",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Permissions = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupAccess", x => new { x.UserId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_GroupAccess_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupAccess_DeviceGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "DeviceGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGroups_Name",
                table: "DeviceGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_GroupId_Name",
                table: "Devices",
                columns: new[] { "GroupId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_Status",
                table: "Devices",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentTokens_CreatedByUserId",
                table: "EnrollmentTokens",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentTokens_GroupId",
                table: "EnrollmentTokens",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentTokens_TokenHash",
                table: "EnrollmentTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupAccess_GroupId",
                table: "GroupAccess",
                column: "GroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "EnrollmentTokens");

            migrationBuilder.DropTable(
                name: "GroupAccess");

            migrationBuilder.DropTable(
                name: "DeviceGroups");
        }
    }
}
