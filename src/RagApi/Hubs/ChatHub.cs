using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using RagApi.Data;
using RagApi.Models;
using RagApi.Options;
using RagApi.Services;

namespace RagApi.Hubs;

public class ChatHub : Hub
{
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chatCompletion;
    private readonly ITeiEmbeddingService _embeddingService;
    private readonly ISqlDocumentRepository _repository;
    private readonly SearchOptions _searchOptions;
    private readonly IMemoryCache _chatMemory;

    public ChatHub(
        Kernel kernel,
        ITeiEmbeddingService embeddingService,
        ISqlDocumentRepository repository,
        IOptions<SearchOptions> searchOptions,
        IMemoryCache chatMemory)
    {
        _kernel = kernel;
        _chatCompletion = kernel.GetRequiredService<IChatCompletionService>();
        _embeddingService = embeddingService;
        _repository = repository;
        _searchOptions = searchOptions.Value;
        _chatMemory = chatMemory;
    }

    public override async Task OnConnectedAsync()
    {
        Console.WriteLine($"Client connected: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        Console.WriteLine($"Client disconnected: {Context.ConnectionId}");
        await base.OnDisconnectedAsync(exception);
    }

    public async Task Ask(string message)
    {
        try
        {
            var cancellationToken = Context.ConnectionAborted;

            if (IsSimpleGreeting(message))
            {
                await SendCompleteAnswerAsync(
                    "Xin chào! Mình có thể giúp bạn hỏi đáp dựa trên tài liệu đã nạp.",
                    [],
                    cancellationToken);
                return;
            }

            var effectiveMessage = ResolveFollowUpMessage(message);
            var requestedTopK = IsStructuredLookup(effectiveMessage)
                ? Math.Clamp(100, 1, Math.Max(1, _searchOptions.TopKMax))
                : Math.Clamp(_searchOptions.ChatTopK, 1, Math.Max(1, _searchOptions.TopKMax));

            var (embedding, _) = await _embeddingService.EmbedQueryCachedAsync(effectiveMessage, cancellationToken);
            var searchResults = await _repository.SearchAsync(effectiveMessage, embedding, requestedTopK, cancellationToken);

            var documents = searchResults.Documents.Count > 0 ? searchResults.Documents[0] : [];
            var citations = BuildFrontendCitations(searchResults);
            var contextText = BuildContextText(documents, searchResults.Metadatas.Count > 0 ? searchResults.Metadatas[0] : []);

            RememberLookupTarget(effectiveMessage);

            if (string.IsNullOrWhiteSpace(contextText))
            {
                await SendCompleteAnswerAsync("Mình chưa tìm thấy thông tin này trong tài liệu đã nạp.", citations, cancellationToken);
                return;
            }

            if (TryAnswerFromStructuredContext(effectiveMessage, documents, out var structuredAnswer))
            {
                await SendCompleteAnswerAsync(structuredAnswer, citations, cancellationToken);
                return;
            }



            const string systemMessage = """
                Bạn là trợ lý hỏi đáp tài liệu nội bộ.

                Luật trả lời:
                - Chỉ dùng thông tin trong NGỮ CẢNH.
                - Không bịa, không thêm kiến thức ngoài tài liệu.
                - Nếu không đủ thông tin, chỉ nói: "Mình chưa tìm thấy thông tin này trong tài liệu đã nạp."
                - Trả lời ngắn gọn, trực tiếp bằng tiếng Việt.
                """;

            var chatHistory = new ChatHistory(systemMessage);
            chatHistory.AddUserMessage($"""
                <NGỮ_CẢNH>
                {contextText}
                </NGỮ_CẢNH>

                Câu hỏi: {effectiveMessage}
                """);

            await Clients.Caller.SendAsync("ReceiveCitations", citations, cancellationToken);
            await Clients.Caller.SendAsync("ChatStarted", cancellationToken);

            var executionSettings = new OpenAIPromptExecutionSettings
            {
                Temperature = 0,
                TopP = 1,
                MaxTokens = 500
            };

            var stream = _chatCompletion.GetStreamingChatMessageContentsAsync(
                chatHistory,
                executionSettings,
                _kernel,
                cancellationToken);

            await foreach (var content in stream)
            {
                if (!string.IsNullOrEmpty(content.Content))
                {
                    await Clients.Caller.SendAsync("ReceiveToken", content.Content, cancellationToken);
                }
            }

            await Clients.Caller.SendAsync("ChatEnded", cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in Ask: {ex}");
            await Clients.Caller.SendAsync("ReceiveToken", "\n[Lỗi kết nối tới mô hình AI hoặc Database]");
            await Clients.Caller.SendAsync("ChatEnded");
        }
    }

    private async Task SendCompleteAnswerAsync(string answer, IReadOnlyList<object> citations, CancellationToken cancellationToken)
    {
        await Clients.Caller.SendAsync("ReceiveCitations", citations, cancellationToken);
        await Clients.Caller.SendAsync("ChatStarted", cancellationToken);
        await Clients.Caller.SendAsync("ReceiveToken", answer, cancellationToken);
        await Clients.Caller.SendAsync("ChatEnded", cancellationToken);
    }

    private string ResolveFollowUpMessage(string message)
    {
        var normalized = NormalizeText(message);
        if ((normalized is "cua ai" or "cua ai vay" or "la cua ai" or "ai vay")
            && _chatMemory.TryGetValue(GetLastLookupKey(), out string? lastLookup)
            && !string.IsNullOrWhiteSpace(lastLookup))
        {
            return $"{lastLookup} là của ai";
        }

        return message;
    }

    private void RememberLookupTarget(string message)
    {
        var id = FindIdentifier(message);
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        _chatMemory.Set(GetLastLookupKey(), id, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15),
            Size = 1
        });
    }

    private string GetLastLookupKey() => $"chat:last-lookup:{Context.ConnectionId}";

    private static List<object> BuildFrontendCitations(SearchResponse searchResults)
    {
        var citations = new List<object>();
        if (searchResults.Metadatas.Count == 0)
        {
            return citations;
        }

        var seenSources = new HashSet<string>();

        foreach (var metadata in searchResults.Metadatas[0])
        {
            var metaDict = new Dictionary<string, object>();
            if (metadata.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in metadata.EnumerateObject())
                {
                    metaDict[prop.Name] = prop.Value.ToString();
                }
            }

            var source = metaDict.TryGetValue("source", out var s) ? s?.ToString() : null;
            if (!string.IsNullOrWhiteSpace(source))
            {
                if (!seenSources.Add(source))
                {
                    continue;
                }
            }

            citations.Add(metaDict);
        }

        return citations;
    }

    private static string BuildContextText(IReadOnlyList<string> documents, IReadOnlyList<JsonElement> metadatas)
    {
        var chunks = new List<string>();
        for (var i = 0; i < documents.Count; i++)
        {
            var metadata = i < metadatas.Count ? metadatas[i] : default;
            chunks.Add($"""
                [ĐOẠN {i + 1} - {BuildSourceLabel(metadata)}]
                {documents[i]}
                """);
        }

        return string.Join("\n\n---\n\n", chunks);
    }

    private static bool TryAnswerFromStructuredContext(string question, IReadOnlyList<string> documents, out string answer)
    {
        answer = string.Empty;
        var normalizedQuestion = NormalizeText(question);
        var id = FindIdentifier(question);
        var nameQuery = ExtractPersonName(question);
        var asksEmail = ContainsAny(normalizedQuestion, "email", "mail", "thu dien tu");
        var asksPerson = ContainsAny(normalizedQuestion, "cua ai", "la ai", "mssv", "ma so id", "ma id", "ma so sinh vien")
            || !string.IsNullOrWhiteSpace(id);

        foreach (var document in documents)
        {
            var row = ParseStructuredRow(document);
            if (row.Count == 0)
            {
                continue;
            }

            var studentId = GetField(row, "ma so id", "mssv", "ma id", "ma so sinh vien", "ma sinh vien", "student id", "id");
            var lastName = GetField(row, "ho", "last name", "surname");
            var firstName = GetField(row, "ten", "first name", "given name");
            var fullName = GetField(row, "ho ten", "hoten", "ten sinh vien", "name", "full name")
                ?? JoinName(lastName, firstName);
            var email = GetField(row, "dia chi thu dien tu", "thu dien tu", "email", "mail", "e-mail");
            var className = GetField(row, "lop", "class");

            var matchesId = !string.IsNullOrWhiteSpace(id)
                && !string.IsNullOrWhiteSpace(studentId)
                && string.Equals(studentId.Trim(), id, StringComparison.OrdinalIgnoreCase);
            var matchesName = !string.IsNullOrWhiteSpace(nameQuery)
                && !string.IsNullOrWhiteSpace(fullName)
                && NormalizeText(fullName).Contains(NormalizeText(nameQuery));

            bool isMatch = false;
            if (!string.IsNullOrWhiteSpace(id))
            {
                isMatch = matchesId;
            }
            else if (!string.IsNullOrWhiteSpace(nameQuery))
            {
                isMatch = matchesName;
            }

            if (!isMatch)
            {
                continue;
            }

            if (asksEmail)
            {
                answer = !string.IsNullOrWhiteSpace(email)
                    ? $"Email của {fullName ?? nameQuery} là {email}."
                    : "Mình tìm thấy người này trong tài liệu nhưng chưa thấy email trong dòng dữ liệu.";
                return true;
            }

            if (asksPerson)
            {
                answer = FormatStudentAnswer(studentId, fullName, email, className);
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(nameQuery))
        {
            answer = "Mình chưa tìm thấy thông tin này trong tài liệu đã nạp.";
            return true;
        }

        return false;
    }

    private static string FormatStudentAnswer(string? studentId, string? fullName, string? email, string? className)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(studentId))
        {
            parts.Add($"Mã số ID: {studentId}");
        }

        if (!string.IsNullOrWhiteSpace(fullName))
        {
            parts.Add($"Họ tên: {fullName}");
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            parts.Add($"Email: {email}");
        }

        if (!string.IsNullOrWhiteSpace(className))
        {
            parts.Add($"Lớp: {className}");
        }

        return parts.Count == 0
            ? "Mình chưa tìm thấy thông tin này trong tài liệu đã nạp."
            : string.Join(", ", parts) + ".";
    }



    private static Dictionary<string, string> ParseStructuredRow(string document)
    {
        var fields = new Dictionary<string, string>();
        foreach (var part in SplitCsvLikeRow(document))
        {
            var separatorIndex = part.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex == part.Length - 1)
            {
                continue;
            }

            var key = NormalizeText(part[..separatorIndex]);
            var value = part[(separatorIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key) && !fields.ContainsKey(key))
            {
                fields[key] = value;
            }
        }

        return fields;
    }

    private static IEnumerable<string> SplitCsvLikeRow(string text)
    {
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in text)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                current.Append(ch);
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                yield return current.ToString().Trim();
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            yield return current.ToString().Trim();
        }
    }

    private static string? GetField(IReadOnlyDictionary<string, string> fields, params string[] aliases)
    {
        var normalizedAliases = aliases.Select(NormalizeText).ToArray();

        foreach (var alias in normalizedAliases)
        {
            if (fields.TryGetValue(alias, out var exactValue))
            {
                return exactValue;
            }
        }

        foreach (var (key, value) in fields)
        {
            if (normalizedAliases.Any(alias => key == alias || key.Contains(alias)))
            {
                return value;
            }
        }

        return null;
    }

    private static string? JoinName(string? lastName, string? firstName)
    {
        var parts = new[] { lastName, firstName }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToArray();

        return parts.Length == 0 ? null : string.Join(' ', parts);
    }

    private static string BuildSourceLabel(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            return "unknown source";
        }

        var source = GetMetadataString(metadata, "source");
        var page = GetMetadataString(metadata, "page");
        var row = GetMetadataString(metadata, "row");

        if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(page))
        {
            return $"{source}, page {page}";
        }

        if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(row))
        {
            return $"{source}, row {row}";
        }

        return string.IsNullOrWhiteSpace(source) ? "unknown source" : source;
    }

    private static string? GetMetadataString(JsonElement metadata, string propertyName)
    {
        return metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(propertyName, out var value)
            ? value.ToString()
            : null;
    }

    private static bool IsSimpleGreeting(string message)
    {
        var normalized = NormalizeText(message);
        return normalized is "hi" or "hello" or "xin chao" or "chao" or "alo" or "a lo";
    }

    private static bool IsStructuredLookup(string message)
    {
        var normalized = NormalizeText(message);
        return FindIdentifier(message) is not null
            || ContainsAny(normalized, "mssv", "ma so id", "ma id", "ma so sinh vien", "email", "mail", "thu dien tu", "lop", "cua ai");
    }

    private static string? FindIdentifier(string text)
    {
        var match = Regex.Match(text, @"\b\d{6,}\b");
        return match.Success ? match.Value : null;
    }

    private static string? ExtractPersonName(string text)
    {
        var normalized = NormalizeText(text);
        if (!ContainsAny(normalized, "email", "mail", "thu dien tu", "cua ai", "la ai"))
        {
            return null;
        }

        var match = Regex.Match(
            text,
            @"(?:email|mail|thư điện tử)\s+(?:của|cua)\s+(.+?)(?:\s+(?:là|la)\s+gì|[?？])?$",
            RegexOptions.IgnoreCase);

        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        match = Regex.Match(text, @"(?:của|cua)\s+(.+?)(?:\s+(?:là|la)\s+gì|[?？])?$", RegexOptions.IgnoreCase);
        var name = match.Success ? match.Groups[1].Value.Trim() : null;
        var normalizedName = name != null ? NormalizeText(name) : null;
        return normalizedName == "ai" || normalizedName == "ai vay" ? null : name;
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string TrimAnswer(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var trimmed = text[..maxLength];
        var lastSpace = trimmed.LastIndexOf(' ');
        return (lastSpace > 0 ? trimmed[..lastSpace] : trimmed).TrimEnd(',', ';', ':') + "...";
    }

    private static string NormalizeText(string text)
    {
        var normalized = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character == 'đ' ? 'd' : character);
            }
        }

        return string.Join(' ', builder.ToString().Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
