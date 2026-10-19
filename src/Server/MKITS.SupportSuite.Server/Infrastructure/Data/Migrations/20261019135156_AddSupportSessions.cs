// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MKITS.SupportSuite.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CodeHash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: true),
                    CodeExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CustomerLabel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AgentMachineName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AgentIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    EndReason = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Recorded = table.Column<bool>(type: "INTEGER", nullable: false),
                    BytesRelayed = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_CodeHash",
                table: "Sessions",
                column: "CodeHash");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_OwnerUserId_CreatedAt",
                table: "Sessions",
                columns: new[] { "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_Status_CodeExpiresAt",
                table: "Sessions",
                columns: new[] { "Status", "CodeExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Sessions");
        }
    }
}
