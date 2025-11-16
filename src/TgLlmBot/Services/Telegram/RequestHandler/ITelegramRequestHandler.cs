using System;
using System.Threading.Tasks;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TgLlmBot.Services.Telegram.RequestHandler;

public interface ITelegramRequestHandler
{
    Task OnMessageAsync(Message message, UpdateType type);
    Task OnErrorAsync(Exception exception, HandleErrorSource source);
    Task OnUpdateAsync(Update update);
}
