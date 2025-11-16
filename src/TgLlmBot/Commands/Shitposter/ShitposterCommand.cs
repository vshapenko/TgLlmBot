using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.Services.Telegram.CommandDispatcher.Abstractions;

namespace TgLlmBot.Commands.Shitposter;

public class ShitposterCommand : AbstractCommand
{
    public ShitposterCommand(Message message, UpdateType type) : base(message, type)
    {
    }
}
