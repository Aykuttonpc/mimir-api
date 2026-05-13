using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mimir.Api.Migrations
{
    /// <summary>
    /// Sprint #14: Conversation entity + members. Mevcut Message satırlarını (SenderId,RecipientId)
    /// çiftinden Conversation+ConversationMember modeline taşır. Mesaj kaybı yok.
    /// </summary>
    public partial class AddConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Yeni tablolar
            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    LastActivityAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "conversation_members",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    LastReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeftAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_members", x => new { x.ConversationId, x.UserId });
                });

            // 2. Messages.ConversationId nullable ekle (backfill için geçici)
            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "messages",
                type: "uuid",
                nullable: true);

            // 3. Backfill — her unique (lo, hi) çifti için 1 DM Conversation + 2 ConversationMember.
            //    Mesajların ConversationId'si bu yeni conv'a set edilir.
            //    LastReadAt: kullanıcının aldığı son okunmuş mesajın CreatedAt'i.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    pair RECORD;
    conv_id UUID;
    lo_last_read TIMESTAMPTZ;
    hi_last_read TIMESTAMPTZ;
BEGIN
    FOR pair IN
        SELECT
            LEAST(""SenderId"", ""RecipientId"") AS lo,
            GREATEST(""SenderId"", ""RecipientId"") AS hi,
            MIN(""CreatedAt"") AS first_at,
            MAX(""CreatedAt"") AS last_at
        FROM messages
        GROUP BY LEAST(""SenderId"", ""RecipientId""), GREATEST(""SenderId"", ""RecipientId"")
    LOOP
        conv_id := gen_random_uuid();

        INSERT INTO conversations (""Id"", ""Type"", ""Name"", ""CreatedById"", ""CreatedAt"", ""LastActivityAt"")
        VALUES (conv_id, 'Dm', NULL, pair.lo, pair.first_at, pair.last_at);

        -- lo'nun en son okuduğu (RecipientId=lo, ReadAt not null max CreatedAt)
        SELECT MAX(""CreatedAt"") INTO lo_last_read
        FROM messages
        WHERE ""RecipientId"" = pair.lo AND ""SenderId"" = pair.hi AND ""ReadAt"" IS NOT NULL;

        SELECT MAX(""CreatedAt"") INTO hi_last_read
        FROM messages
        WHERE ""RecipientId"" = pair.hi AND ""SenderId"" = pair.lo AND ""ReadAt"" IS NOT NULL;

        INSERT INTO conversation_members (""ConversationId"", ""UserId"", ""Role"", ""JoinedAt"", ""LastReadAt"", ""LeftAt"")
        VALUES
            (conv_id, pair.lo, 'Member', pair.first_at, lo_last_read, NULL),
            (conv_id, pair.hi, 'Member', pair.first_at, hi_last_read, NULL);

        UPDATE messages
        SET ""ConversationId"" = conv_id
        WHERE LEAST(""SenderId"", ""RecipientId"") = pair.lo
          AND GREATEST(""SenderId"", ""RecipientId"") = pair.hi;
    END LOOP;
END $$;
");

            // 4. Eski indeksleri + kolonları sil
            migrationBuilder.DropIndex(
                name: "IX_messages_RecipientId_ReadAt",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_messages_RecipientId_SenderId_CreatedAt",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_messages_SenderId_RecipientId_CreatedAt",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "ReadAt",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "RecipientId",
                table: "messages");

            // 5. ConversationId artık NOT NULL
            migrationBuilder.AlterColumn<Guid>(
                name: "ConversationId",
                table: "messages",
                type: "uuid",
                nullable: false,
                defaultValue: Guid.Empty,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            // 6. Yeni indeksler
            migrationBuilder.CreateIndex(
                name: "IX_messages_ConversationId_CreatedAt",
                table: "messages",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_messages_SenderId",
                table: "messages",
                column: "SenderId");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_members_ConversationId",
                table: "conversation_members",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_members_UserId_LeftAt",
                table: "conversation_members",
                columns: new[] { "UserId", "LeftAt" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_LastActivityAt",
                table: "conversations",
                column: "LastActivityAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Note: rollback DM mesajlarını çiftlere geri taşır; group mesajları varsa veri kaybolur.
            // Group oluşturulduktan sonra rollback edilmemeli.

            migrationBuilder.DropIndex(
                name: "IX_messages_ConversationId_CreatedAt",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_messages_SenderId",
                table: "messages");

            // RecipientId + ReadAt geri ekle
            migrationBuilder.AddColumn<Guid>(
                name: "RecipientId",
                table: "messages",
                type: "uuid",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReadAt",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);

            // DM conv için RecipientId backfill — "diğer üye"
            migrationBuilder.Sql(@"
UPDATE messages m
SET ""RecipientId"" = (
    SELECT cm.""UserId""
    FROM conversation_members cm
    WHERE cm.""ConversationId"" = m.""ConversationId""
      AND cm.""UserId"" <> m.""SenderId""
    LIMIT 1
)
WHERE EXISTS (
    SELECT 1 FROM conversations c
    WHERE c.""Id"" = m.""ConversationId"" AND c.""Type"" = 'Dm'
);
");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "messages");

            migrationBuilder.DropTable(
                name: "conversation_members");

            migrationBuilder.DropTable(
                name: "conversations");

            migrationBuilder.CreateIndex(
                name: "IX_messages_RecipientId_ReadAt",
                table: "messages",
                columns: new[] { "RecipientId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_messages_RecipientId_SenderId_CreatedAt",
                table: "messages",
                columns: new[] { "RecipientId", "SenderId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_messages_SenderId_RecipientId_CreatedAt",
                table: "messages",
                columns: new[] { "SenderId", "RecipientId", "CreatedAt" });
        }
    }
}
