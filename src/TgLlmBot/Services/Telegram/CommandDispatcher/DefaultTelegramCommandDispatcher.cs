using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.Commands.ChatWithLlm;
using TgLlmBot.Commands.DisplayHelp;
using TgLlmBot.Services.Telegram.SelfInformation;

namespace TgLlmBot.Services.Telegram.CommandDispatcher;

public class DefaultTelegramCommandDispatcher : ITelegramCommandDispatcher
{
    private readonly ChatWithLlmCommandHandler _chatWithLlmCommandHandler;
    private readonly DisplayHelpCommandHandler _displayHelpCommandHandler;
    private readonly DefaultTelegramCommandDispatcherOptions _options;
    private readonly ITelegramSelfInformation _selfInformation;

    public DefaultTelegramCommandDispatcher(
        DefaultTelegramCommandDispatcherOptions options,
        ITelegramSelfInformation selfInformation,
        DisplayHelpCommandHandler displayHelpCommandHandler,
        ChatWithLlmCommandHandler chatWithLlmCommandHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(selfInformation);
        ArgumentNullException.ThrowIfNull(displayHelpCommandHandler);
        ArgumentNullException.ThrowIfNull(chatWithLlmCommandHandler);
        _options = options;
        _selfInformation = selfInformation;
        _displayHelpCommandHandler = displayHelpCommandHandler;
        _chatWithLlmCommandHandler = chatWithLlmCommandHandler;
    }

    public async Task HandleMessageAsync(Message? message, UpdateType type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (message is null)
        {
            return;
        }

        switch (message.Text)
        {
            case "!help":
                {
                    var command = new DisplayHelpCommand(message, type);
                    await _displayHelpCommandHandler.HandleAsync(command, cancellationToken);
                    return;
                }
        }

        var self = _selfInformation.GetSelf();
        var prompt = message.Text ?? message.Caption;
        if (message.Chat.Type == ChatType.Private)
        {
            var command = new ChatWithLlmCommand(message, type, self, prompt);
            await _chatWithLlmCommandHandler.HandleAsync(command, cancellationToken);
            return;
        }

        if (message.Chat.Type is ChatType.Group or ChatType.Supergroup)
        {
            if (prompt?.StartsWith(_options.BotName, StringComparison.InvariantCultureIgnoreCase) is true)
            {
                var command = new ChatWithLlmCommand(message, type, self, prompt);
                await _chatWithLlmCommandHandler.HandleAsync(command, cancellationToken);
            }

            if (message.ReplyToMessage?.From is not null && message.ReplyToMessage.From.Id == self.Id)
            {
                var command = new ChatWithLlmCommand(message, type, self, prompt);
                await _chatWithLlmCommandHandler.HandleAsync(command, cancellationToken);
            }
        }
    }
}
