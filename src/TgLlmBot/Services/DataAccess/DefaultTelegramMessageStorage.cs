using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Telegram.Bot.Types;
using TgLlmBot.DataAccess;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.Services.DataAccess;

public class DefaultTelegramMessageStorage : ITelegramMessageStorage
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public DefaultTelegramMessageStorage(IServiceScopeFactory serviceScopeFactory)
    {
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        _serviceScopeFactory = serviceScopeFactory;
    }

    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    public async Task StoreMessageAsync(Message message, User self, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            await using (var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                var dbChatMessage = CreateDbChatMessage(message, self);
                dbContext.ChatHistory.Add(dbChatMessage);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
    }

    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    [SuppressMessage("Usage", "CA2241:Provide correct arguments to formatting methods")]
    public async Task<DbChatMessage[]> SelectContextMessagesAsync(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        var resultAccumulator = new List<DbChatMessage>();
        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            await using (var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                var messageId = new NpgsqlParameter("messageId", message.MessageId);
                var chatId = new NpgsqlParameter("chatId", message.Chat.Id);
                var sql = FormattableStringFactory.Create(
                    """
                    WITH target_message AS (
                        SELECT "Date" as cutoff_date
                        FROM public."ChatHistory"
                        WHERE "MessageId" = @messageId AND "ChatId" = @chatId
                    )
                    SELECT
                        "Id",
                        "MessageId",
                        "ChatId",
                        "MessageThreadId",
                        "ReplyToMessageId",
                        "Date",
                        "FromUserId",
                        "FromUsername",
                        "FromFirstName",
                        "FromLastName",
                        "Text",
                        "Caption",
                        "IsLlmReplyToMessage"
                    FROM (
                             SELECT
                                 "Id",
                                 "MessageId",
                                 "ChatId",
                                 "MessageThreadId",
                                 "ReplyToMessageId",
                                 "Date",
                                 "FromUserId",
                                 "FromUsername",
                                 "FromFirstName",
                                 "FromLastName",
                                 "Text",
                                 "Caption",
                                 "IsLlmReplyToMessage",
                                 SUM(COALESCE(LENGTH("Text"), 0) + COALESCE(LENGTH("Caption"), 0)) OVER (
                                     ORDER BY "Date" DESC
                                     ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                                     ) as cumulative_length
                             FROM public."ChatHistory" ch
                             WHERE "ChatId" = @chatId
                               AND "Date" <= (SELECT cutoff_date FROM target_message)
                               AND "MessageId" != @messageId
                               AND NOT EXISTS (
                                    SELECT 1
                                    FROM public."KickedUsers" k
                                    WHERE k."ChatId" = ch."ChatId"
                                      AND k."Id" = ch."FromUserId"
                               )
                             ORDER BY "Date" DESC
                             LIMIT 200
                         ) as subquery
                    WHERE cumulative_length <= 30000
                    ORDER BY "Date" DESC;
                    """,
                    messageId,
                    chatId);
                var dbResults = await dbContext.ChatHistory.FromSql(sql).AsNoTracking().ToListAsync(cancellationToken);
                resultAccumulator.AddRange(dbResults.OrderBy(x => x.Date));
                await transaction.CommitAsync(cancellationToken);
            }
        }

        return resultAccumulator.ToArray();
    }

    private static DbChatMessage CreateDbChatMessage(Message message, User self)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(self);
        var isSelfMessage = self.Id == message.From?.Id;
        return new(
            message.Id,
            message.Chat.Id,
            message.MessageThreadId,
            message.ReplyToMessage?.Id,
            message.Date,
            message.From?.Id,
            message.From?.Username,
            message.From?.FirstName,
            message.From?.LastName,
            message.Text,
            message.Caption,
            isSelfMessage);
    }
}
