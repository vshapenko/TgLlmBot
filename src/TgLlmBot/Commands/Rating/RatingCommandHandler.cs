using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.DataAccess;
using TgLlmBot.Services.Telegram.Markdown;

namespace TgLlmBot.Commands.Rating;

public partial class RatingCommandHandler : AbstractCommandHandler<RatingCommand>
{
    private readonly TelegramBotClient _bot;
    private readonly IChatClient _chatClient;
    private readonly ITelegramMarkdownConverter _markdownConverter;
    private readonly ITelegramMessageStorage _storage;

    public RatingCommandHandler(
        TelegramBotClient bot,
        ITelegramMessageStorage storage,
        ITelegramMarkdownConverter markdownConverter,
        IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(markdownConverter);
        ArgumentNullException.ThrowIfNull(chatClient);
        _bot = bot;
        _storage = storage;
        _markdownConverter = markdownConverter;
        _chatClient = chatClient;
    }

    [GeneratedRegex(@"\p{So}|\p{Sk}")]
    private static partial Regex EmojiRegex();

    public override async Task HandleAsync(RatingCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(command);

        // Get recent context messages
        var contextMessages = await _storage.SelectContextMessagesAsync(
            command.Message,
            cancellationToken);

        // Group messages by user
        var userMessages = contextMessages
            .Where(m => m.FromUserId.HasValue) // Only users with IDs
            .Where(m => !m.IsLlmReplyToMessage) // Exclude bot messages
            .GroupBy(m => new
            {
                m.FromUserId
            })
            .ToList();

        // Analyze each user with pattern-based scoring
        var userStatsWithScores = new List<UserShitpostStats>();

        foreach (var userGroup in userMessages)
        {
            var messages = userGroup.ToList();
            var patternScore = CalculatePatternScore(messages);

            // Sample messages for LLM analysis (max 5 per user to control costs)
            // Take evenly distributed samples
            var sampleSize = Math.Min(5, messages.Count);
            var step = messages.Count / sampleSize;
            var sampleMessages = new List<DbChatMessage>();
            for (var i = 0; i < sampleSize; i++)
            {
                sampleMessages.Add(messages[i * step]);
            }

            var llmScore = await AnalyzeMessagesWithLlmAsync(sampleMessages, cancellationToken);

            // Combined score: 60% pattern-based, 40% LLM-based
            var combinedScore = (patternScore * 0.6) + (llmScore * 0.4);

            var lastMessage = userGroup.Last();
            var username = lastMessage.FromUsername;
            var firstName = lastMessage.FromFirstName;
            var lastName = lastMessage.FromLastName;

            userStatsWithScores.Add(new(
                userGroup.Key.FromUserId!.Value,
                username,
                firstName,
                lastName,
                messages.Count,
                messages.Average(m => (m.Text?.Length ?? 0) + (m.Caption?.Length ?? 0)),
                patternScore,
                llmScore,
                combinedScore));
        }

        // Sort by combined score
        var rankedUsers = userStatsWithScores.OrderByDescending(x => x.CombinedScore).ToList();

        // Build response
        var response = BuildShitposterReport(rankedUsers, contextMessages.Length);
        var markdownResponse = _markdownConverter.ConvertToTelegramMarkdown(response);

        await _bot.SendMessage(
            command.Message.Chat,
            markdownResponse,
            ParseMode.MarkdownV2,
            new()
            {
                MessageId = command.Message.MessageId
            },
            cancellationToken: cancellationToken);
    }

    private static double CalculatePatternScore(List<DbChatMessage> messages)
    {
        double totalScore = 0;
        var scoredMessages = 0;

        foreach (var msg in messages)
        {
            var text = msg.Text ?? msg.Caption ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            double messageScore = 0;

            // Very short messages (1-10 chars) = high shitpost indicator
            if (text.Length <= 10)
            {
                messageScore += 30;
            }
            else if (text.Length <= 20)
            {
                messageScore += 15;
            }

            // Emoji density
            var emojiCount = EmojiRegex().Matches(text).Count;
            var emojiDensity = text.Length > 0 ? (double) emojiCount / text.Length : 0;
            messageScore += emojiDensity * 50;

            // Excessive punctuation (!!!, ???, etc.)
            var exclamationCount = text.Count(c => c == '!');
            var questionCount = text.Count(c => c == '?');
            if (exclamationCount > 2 || questionCount > 2)
            {
                messageScore += 10;
            }

            // All caps (excluding short messages) - works for both Latin and Cyrillic
            var uppercaseCount = text.Count(c => char.IsUpper(c) || (c >= 'А' && c <= 'Я'));
            var letterCount = text.Count(c => char.IsLetter(c));
            if (letterCount > 5 && uppercaseCount > letterCount * 0.7)
            {
                messageScore += 20;
            }

            // Repetitive characters (lol, haha, etc.)
            if (text.Length > 2 && HasRepetitivePattern(text))
            {
                messageScore += 15;
            }

            // Common Russian shitpost words/phrases
            var lowerText = text.ToLowerInvariant();
            string[] russianShitpostWords = [
                "лол", "кек", "пепе", "жиза", "жесть", "угар", "ору", "ржу", "орнул",
                "кринж", "краш", "агонь", "ахуе", "збс", "топ", "база", "кек чебурек",
                "бомбит", "триггер", "рофл", "лул", "лмао", "пон", "хз", "имхо", "кмк",
                "азаза", "ахах", "ебать", "бля", "пздц", "епт", "ога", "пффф", "ясно",
                "++", "тру", "го", "го го", "не", "ок", "окей", "найс", "гг"
            ];

            var matchedWords = russianShitpostWords.Count(word =>
                lowerText.Contains(word, StringComparison.Ordinal));
            messageScore += matchedWords * 10;

            totalScore += Math.Min(messageScore, 100); // Cap at 100 per message
            scoredMessages++;
        }

        return scoredMessages > 0 ? totalScore / scoredMessages : 0;
    }

    private static bool HasRepetitivePattern(string text)
    {
        var lower = text.ToLowerInvariant();

        // Check for repeated sequences (English and Russian)
        string[] patterns = [
            // English
            "ha", "he", "lo", "ke",
            // Russian laughter
            "ха", "хе", "хи", "хо", "ах", "ух", "эх", "ох",
            // Russian shitpost patterns
            "бля", "лол", "кек", "пфф", "ого", "вау", "жиза", "ага",
            // Repetitive sounds
            "ыы", "аа", "оо", "ее", "уу"
        ];

        foreach (var pattern in patterns)
        {
            var count = 0;
            var index = 0;
            while ((index = lower.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
            {
                count++;
                index += pattern.Length;
            }

            if (count >= 3) // "хахаха" or more
            {
                return true;
            }
        }

        return false;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    private async Task<double> AnalyzeMessagesWithLlmAsync(List<DbChatMessage> messages, CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
        {
            return 0;
        }

        // Build sample text
        var sampleBuilder = new StringBuilder();
        foreach (var msg in messages)
        {
            var text = msg.Text ?? msg.Caption;
            if (!string.IsNullOrWhiteSpace(text))
            {
                sampleBuilder.AppendLine(CultureInfo.InvariantCulture, $"- {text}");
            }
        }

        var prompt = $"""
                      Проанализируй эти сообщения и оцени "качество шитпостинга" по шкале от 0 до 100.

                      Признаки шитпостинга:
                      - Бессмысленные или провокационные реплики
                      - Спам похожими сообщениями
                      - Спам эмодзи, капслок
                      - Низкокачественный юмор, "угар", мемы

                      Сообщения:
                      {sampleBuilder}

                      Ответь ТОЛЬКО числом от 0 до 100, где:
                      - 0 = серьёзное, качественное обсуждение
                      - 50 = смесь серьёзного и шуток
                      - 100 = чистый шитпостинг/мемы

                      Твой ответ (только число):
                      """;

        try
        {
            var response = await _chatClient.GetResponseAsync(prompt, new()
            {
                Temperature = 0.3f,
                MaxOutputTokens = 10
            }, cancellationToken);

            var scoreText = response.Text?.Trim() ?? "0";

            // Extract first number found
            var match = Regex.Match(scoreText, @"\d+");
            if (match.Success && double.TryParse(match.Value, out var score))
            {
                return Math.Clamp(score, 0, 100);
            }

            return 0;
        }
        catch (Exception)
        {
            // If LLM fails, return neutral score
            return 50;
        }
    }

    private static string BuildShitposterReport(List<UserShitpostStats> userStats, int totalMessages)
    {
        if (userStats.Count == 0)
        {
            return "Нет сообщений для анализа 🤷";
        }

        var builder = new StringBuilder();
        builder.AppendLine("🎭 **Рейтинг Щитпостеров**");
        builder.AppendLine("_Semantic analysis enabled_");
        builder.AppendLine();

        var top5 = userStats.Take(5).ToList();
        for (var i = 0; i < top5.Count; i++)
        {
            var user = top5[i];
            var rank = i + 1;
            var medal = rank switch
            {
                1 => "🥇",
                2 => "🥈",
                3 => "🥉",
                _ => "  "
            };

            var name = user.Username;
            if (string.IsNullOrWhiteSpace(name))
            {
                var combinedName = $"{user.FirstName?.Trim()} {user.LastName?.Trim()}".Trim();
                name = !string.IsNullOrWhiteSpace(combinedName)
                    ? combinedName
                    : "Anonymous";
            }

            var percentage = user.MessageCount * 100.0 / totalMessages;
            builder.AppendLine(CultureInfo.InvariantCulture, $"{medal} #{rank}: `{name}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"   Уровень кринжа: {user.CombinedScore:F1}/100");
            builder.AppendLine(CultureInfo.InvariantCulture, $"   Сообщений: {user.MessageCount} ({percentage:F1}%)");
            builder.AppendLine(CultureInfo.InvariantCulture, $"   Паттерны: {user.PatternScore:F0} | LLM: {user.LlmScore:F0}");
            builder.AppendLine();
        }

        builder.AppendLine(CultureInfo.InvariantCulture, $"_Проанализировано {totalMessages} сообщений_");

        return builder.ToString();
    }

    private sealed class UserShitpostStats
    {
        public UserShitpostStats(
            long userId,
            string? username,
            string? firstName,
            string? lastName,
            int messageCount,
            double avgLength,
            double patternScore,
            double llmScore,
            double combinedScore)
        {
            UserId = userId;
            Username = username;
            FirstName = firstName;
            LastName = lastName;
            MessageCount = messageCount;
            AvgLength = avgLength;
            PatternScore = patternScore;
            LlmScore = llmScore;
            CombinedScore = combinedScore;
        }

        public long UserId { get; }
        public string? Username { get; }
        public string? FirstName { get; }
        public string? LastName { get; }
        public int MessageCount { get; }
        public double AvgLength { get; }
        public double PatternScore { get; }
        public double LlmScore { get; }
        public double CombinedScore { get; }
    }
}
