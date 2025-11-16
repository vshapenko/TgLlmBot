using System.Threading;
using System.Threading.Tasks;

namespace TgLlmBot.Services.DataAccess;

public interface ITelegramKickedUsersStorage
{
    Task StoreKickedUserAsync(long chatId, long userId, CancellationToken cancellationToken);

    Task RemoveKickedUserAsync(long chatId, long userId, CancellationToken cancellationToken);
}
