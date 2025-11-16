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
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.DataAccess;
using TgLlmBot.Services.Telegram.CommandDispatcher.Abstractions;
using TgLlmBot.Services.Telegram.Markdown;

namespace TgLlmBot.Commands.Shitposter;

public partial class ShitposterCommandHandler : AbstractCommandHandler<ShitposterCommand>
{
    private readonly TelegramBotClient _bot;
    private readonly ITelegramMessageStorage _storage;
    private readonly ITelegramMarkdownConverter _markdownConverter;
    private readonly IChatClient _chatClient;

    [GeneratedRegex(@"\p{So}|\p{Sk}")]
    private static partial Regex EmojiRegex();

    public ShitposterCommandHandler(
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

    public override async Task HandleAsync(ShitposterCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(command);

        // Get recent context messages
        var contextMessages = await _storage.SelectContextMessagesAsync(
            command.Message,
            cancellationToken);

        // Group messages by user
        var userMessages = contextMessages
            .Where(m => !m.IsLlmReplyToMessage) // Exclude bot messages
            .Where(m => m.FromUserId.HasValue) // Only users with IDs
            .GroupBy(m => new { m.FromUserId, m.FromUsername, m.FromFirstName, m.FromLastName })
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
            for (int i = 0; i < sampleSize; i++)
            {
                sampleMessages.Add(messages[i * step]);
            }

            var llmScore = await AnalyzeMessagesWithLlm(sampleMessages, cancellationToken);

            // Combined score: 60% pattern-based, 40% LLM-based
            var combinedScore = (patternScore * 0.6) + (llmScore * 0.4);

            userStatsWithScores.Add(new UserShitpostStats
            {
                UserId = userGroup.Key.FromUserId!.Value,
                Username = userGroup.Key.FromUsername,
                FirstName = userGroup.Key.FromFirstName,
                LastName = userGroup.Key.FromLastName,
                MessageCount = messages.Count,
                AvgLength = messages.Average(m => (m.Text?.Length ?? 0) + (m.Caption?.Length ?? 0)),
                PatternScore = patternScore,
                LlmScore = llmScore,
                CombinedScore = combinedScore
            });
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
            replyParameters: new()
            {
                MessageId = command.Message.MessageId
            },
            cancellationToken: cancellationToken);
    }

    private static double CalculatePatternScore(List<DbChatMessage> messages)
    {
        double totalScore = 0;
        int scoredMessages = 0;

        foreach (var msg in messages)
        {
            var text = msg.Text ?? msg.Caption ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            double messageScore = 0;

            // Very short messages (1-10 chars) = high shitpost indicator
            if (text.Length <= 10)
                messageScore += 30;
            else if (text.Length <= 20)
                messageScore += 15;

            // Emoji density
            var emojiCount = EmojiRegex().Matches(text).Count;
            var emojiDensity = text.Length > 0 ? (double)emojiCount / text.Length : 0;
            messageScore += emojiDensity * 50;

            // Excessive punctuation (!!!, ???, etc.)
            var exclamationCount = text.Count(c => c == '!');
            var questionCount = text.Count(c => c == '?');
            if (exclamationCount > 2 || questionCount > 2)
                messageScore += 10;

            // All caps (excluding short messages)
            if (text.Length > 5 && text.Count(char.IsUpper) > text.Length * 0.7)
                messageScore += 20;

            // Repetitive characters (lol, haha, etc.)
            if (text.Length > 2 && HasRepetitivePattern(text))
                messageScore += 15;

            totalScore += Math.Min(messageScore, 100); // Cap at 100 per message
            scoredMessages++;
        }

        return scoredMessages > 0 ? totalScore / scoredMessages : 0;
    }

    private static bool HasRepetitivePattern(string text)
    {
        var lower = text.ToLowerInvariant();

        // Check for repeated sequences
        string[] patterns = ["ha", "he", "xa", "lo", "ke", "ах", "ха", "хе"];
        foreach (var pattern in patterns)
        {
            var count = 0;
            var index = 0;
            while ((index = lower.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
            {
                count++;
                index += pattern.Length;
            }
            if (count >= 3) // "hahaha" or more
                return true;
        }

        return false;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    private async Task<double> AnalyzeMessagesWithLlm(List<DbChatMessage> messages, CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
            return 0;

        // Build sample text
        var sampleBuilder = new StringBuilder();
        foreach (var msg in messages)
        {
            var text = msg.Text ?? msg.Caption;
            if (!string.IsNullOrWhiteSpace(text))
                sampleBuilder.AppendLine(CultureInfo.InvariantCulture, $"- {text}");
        }

        var prompt = $"""
            Analyze these messages and rate the "shitposting quality" from 0-100.

            Shitposting indicators:
            - Memes, jokes, sarcasm, humor
            - Low-effort content
            - Non-serious messages

            Messages:
            {sampleBuilder}

            Respond with ONLY a number from 0-100, where:
            - 0 = serious, high-quality discussion
            - 50 = mix of serious and jokes
            - 100 = pure shitposting/memes

            Your response (number only):
            """;

        try
        {
            var response = await _chatClient.GetResponseAsync(prompt, new ChatOptions
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
        builder.AppendLine("🎭 **Рейтинг Шитпостеров**");
        builder.AppendLine("_Semantic analysis enabled_");
        builder.AppendLine();

        var top5 = userStats.Take(5).ToList();
        for (int i = 0; i < top5.Count; i++)
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

            var name = user.Username ?? user.FirstName ?? "Anonymous";
            var percentage = (user.MessageCount * 100.0 / totalMessages);

            builder.AppendLine(CultureInfo.InvariantCulture, $"{medal} #{rank}: `{name}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"   Качество шитпоста: {user.CombinedScore:F1}/100");
            builder.AppendLine(CultureInfo.InvariantCulture, $"   Сообщений: {user.MessageCount} ({percentage:F1}%)");
            builder.AppendLine(CultureInfo.InvariantCulture, $"   Паттерны: {user.PatternScore:F0} | LLM: {user.LlmScore:F0}");
            builder.AppendLine();
        }

        builder.AppendLine(CultureInfo.InvariantCulture, $"_Проанализировано {totalMessages} сообщений_");

        return builder.ToString();
    }

    private sealed class UserShitpostStats
    {
        public long UserId { get; init; }
        public string? Username { get; init; }
        public string? FirstName { get; init; }
        public string? LastName { get; init; }
        public int MessageCount { get; init; }
        public double AvgLength { get; init; }
        public double PatternScore { get; init; }
        public double LlmScore { get; init; }
        public double CombinedScore { get; init; }
    }
}
