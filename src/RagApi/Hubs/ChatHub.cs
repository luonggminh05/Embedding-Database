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
                    "Xin ch\u00E0o! M\u00ECnh c\u00F3 th\u1EC3 gi\u00FAp b\u1EA1n h\u1ECFi \u0111\u00E1p d\u1EF1a tr\u00EAn t\u00E0i li\u1EC7u \u0111\u00E3 n\u1EA1p.",
                    [],
                    cancellationToken);
                return;
            }

            var effectiveMessage = ResolveFollowUpMessage(message);
            var requestedTopK = IsStructuredLookup(effectiveMessage)
                ? Math.Clamp(100, 1, Math.Max(1, _searchOptions.TopKMax))
                : Math.Clamp(_searchOptions.ChatTopK, 1, Math.Max(1, _searchOptions.TopKMax));

            var queryIntent = QueryClassifier.Classify(effectiveMessage);
            var primaryContentKind = GetPrimaryContentKind(queryIntent);
            var relatedContentKind = GetRelatedContentKind(queryIntent);

            var (embedding, _) = await _embeddingService.EmbedQueryCachedAsync(effectiveMessage, cancellationToken);
            var searchResults = await _repository.SearchAsync(effectiveMessage, embedding, requestedTopK, cancellationToken, primaryContentKind);

            var documents = searchResults.Documents.Count > 0 ? searchResults.Documents[0].ToList() : [];
            var metadatas = searchResults.Metadatas.Count > 0 ? searchResults.Metadatas[0].ToList() : [];

            if (documents.Count == 0 && primaryContentKind != null)
            {
                searchResults = await _repository.SearchAsync(effectiveMessage, embedding, requestedTopK, cancellationToken);
                documents = searchResults.Documents.Count > 0 ? searchResults.Documents[0].ToList() : [];
                metadatas = searchResults.Metadatas.Count > 0 ? searchResults.Metadatas[0].ToList() : [];
            }

            if (relatedContentKind != null && metadatas.Count > 0)
            {
                var relatedChunks = await _repository.GetRelatedChunksAsync(
                    metadatas,
                    relatedContentKind,
                    Math.Clamp(_searchOptions.ChatTopK, 1, 10),
                    cancellationToken);

                AppendRelatedChunks(documents, metadatas, relatedChunks);
            }

            var citations = BuildFrontendCitations(searchResults);
            var contextText = BuildContextText(documents, metadatas, queryIntent);

            RememberLookupTarget(effectiveMessage);

            if (string.IsNullOrWhiteSpace(contextText))
            {
                await SendCompleteAnswerAsync("M\u00ECnh ch\u01B0a t\u00ECm th\u1EA5y th\u00F4ng tin n\u00E0y trong t\u00E0i li\u1EC7u \u0111\u00E3 n\u1EA1p.", citations, cancellationToken);
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
                - Nếu ngữ cảnh có thông tin liên quan, hãy trả lời bằng phần tìm thấy. Nếu thiếu một phần, nói rõ phần nào chưa thấy trong tài liệu.
                - Trả lời ngắn gọn, đủ thông tin, trực tiếp bằng tiếng Việt tự nhiên.
                - Không lặp lại cùng một câu, đoạn, ví dụ, hoặc lời kết.
                - Khi hỏi về thao tác phần mềm, ưu tiên các bước cụ thể, tên menu, trường, nút, panel, tab trong ngữ cảnh.
                - Giữ câu kết, nếu có, tối đa một câu ngắn.

                Luật ưu tiên nội dung:
                - NGỮ CẢNH có thể có hai loại đoạn: [VĂN BẢN HƯỚNG DẪN] và [MÔ TẢ GIAO DIỆN TỪ HÌNH ẢNH].
                - Khi người dùng hỏi CÁCH THAO TÁC, hãy ưu tiên trả lời từ [VĂN BẢN HƯỚNG DẪN]. Chỉ dùng [MÔ TẢ GIAO DIỆN TỪ HÌNH ẢNH] để bổ sung vị trí nút/trường nếu cần.
                - Khi người dùng hỏi VỊ TRÍ hoặc GIAO DIỆN, hãy dùng [MÔ TẢ GIAO DIỆN TỪ HÌNH ẢNH] để trả lời, và dùng text hướng dẫn cùng trang/slide để hiểu bối cảnh.
                - Không để mô tả giao diện từ hình ảnh thay thế hoặc lấn át các bước thao tác gốc nếu văn bản hướng dẫn đã đề cập.
                """;

            var chatHistory = new ChatHistory(systemMessage);
            chatHistory.AddUserMessage($"""
                <NG\u1EEE_C\u1EA2NH>
                {contextText}
                </NG\u1EEE_C\u1EA2NH>

                C\u00E2u h\u1ECFi: {effectiveMessage}
                """);

            await Clients.Caller.SendAsync("ReceiveCitations", citations, cancellationToken);
            await Clients.Caller.SendAsync("ChatStarted", cancellationToken);

            var executionSettings = new OpenAIPromptExecutionSettings
            {
                Temperature = 0,
                TopP = 1,
                FrequencyPenalty = 0.6,
                PresencePenalty = 0,
                MaxTokens = 350,
                StopSequences = ["\nNgu\u1ED3n t\u00E0i li\u1EC7u:"]
            };

            var stream = _chatCompletion.GetStreamingChatMessageContentsAsync(
                chatHistory,
                executionSettings,
                _kernel,
                cancellationToken);

            var generatedAnswer = new StringBuilder();
            await foreach (var content in stream)
            {
                if (!string.IsNullOrEmpty(content.Content))
                {
                    generatedAnswer.Append(content.Content);
                    if (IsRepetitionLoop(generatedAnswer.ToString()))
                    {
                        Console.WriteLine("Stopped streaming answer because repeated text was detected.");
                        break;
                    }

                    await Clients.Caller.SendAsync("ReceiveToken", content.Content, cancellationToken);
                }
            }

            await Clients.Caller.SendAsync("ChatEnded", cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in Ask: {ex}");
            await Clients.Caller.SendAsync("ReceiveToken", "\n[L\u1ED7i k\u1EBFt n\u1ED1i t\u1EDBi m\u00F4 h\u00ECnh AI ho\u1EB7c Database]");
            await Clients.Caller.SendAsync("ChatEnded");
        }
    }

    private static bool IsRepetitionLoop(string text)
    {
        if (text.Length < 180)
        {
            return false;
        }

        return HasRepeatedRecentSentence(text) || HasRepeatedSuffix(text, 70, 3);
    }

    private static bool HasRepeatedRecentSentence(string text)
    {
        var sentences = Regex.Split(text.Trim(), @"(?<=[.!?])\s+")
            .Select(sentence => NormalizeText(sentence).Trim())
            .Where(sentence => sentence.Length >= 35)
            .ToArray();

        if (sentences.Length < 4)
        {
            return false;
        }

        var last = sentences[^1];
        return sentences.Count(sentence => sentence == last) >= 3;
    }

    private static bool HasRepeatedSuffix(string text, int suffixLength, int minimumOccurrences)
    {
        var normalized = NormalizeText(Regex.Replace(text, @"\s+", " "));
        if (normalized.Length < suffixLength * minimumOccurrences)
        {
            return false;
        }

        var suffix = normalized[^suffixLength..];
        var count = 0;
        var index = 0;
        while ((index = normalized.IndexOf(suffix, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            if (count >= minimumOccurrences)
            {
                return true;
            }

            index += suffix.Length;
        }

        return false;
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
            return $"{lastLookup} l\u00E0 c\u1EE7a ai";
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


    private static string? GetPrimaryContentKind(QueryIntent intent) => intent switch
    {
        QueryIntent.TaskInstruction => "instruction_text",
        QueryIntent.UiLocation => "image_ui_description",
        _ => null
    };

    private static string? GetRelatedContentKind(QueryIntent intent) => intent switch
    {
        QueryIntent.TaskInstruction => "image_ui_description",
        QueryIntent.UiLocation => "instruction_text",
        _ => null
    };

    private static void AppendRelatedChunks(
        List<string> documents,
        List<JsonElement> metadatas,
        IReadOnlyList<(string Document, JsonElement Metadata)> relatedChunks)
    {
        var seen = new HashSet<string>(documents.Select(NormalizeText), StringComparer.Ordinal);

        foreach (var (document, metadata) in relatedChunks)
        {
            if (string.IsNullOrWhiteSpace(document) || !seen.Add(NormalizeText(document)))
            {
                continue;
            }

            documents.Add(document);
            metadatas.Add(metadata);
        }
    }
    private static string BuildContextText(IReadOnlyList<string> documents, IReadOnlyList<JsonElement> metadatas, QueryIntent intent)
    {
        // Partition documents into instruction_text and image_ui_description
        var instructionChunks = new List<(string document, JsonElement metadata)>();
        var imageChunks = new List<(string document, JsonElement metadata)>();
        var otherChunks = new List<(string document, JsonElement metadata)>();
        var seenDocuments = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            var documentKey = NormalizeText(document);
            if (!seenDocuments.Add(documentKey))
            {
                continue;
            }

            var metadata = i < metadatas.Count ? metadatas[i] : default;
            var contentKind = GetMetadataString(metadata, "content_kind");

            if (string.Equals(contentKind, "image_ui_description", StringComparison.OrdinalIgnoreCase))
            {
                imageChunks.Add((document, metadata));
            }
            else if (string.Equals(contentKind, "instruction_text", StringComparison.OrdinalIgnoreCase))
            {
                instructionChunks.Add((document, metadata));
            }
            else
            {
                otherChunks.Add((document, metadata));
            }
        }

        var sections = new List<string>();
        var chunkNumber = 0;

        // Determine block ordering based on query intent
        var primaryChunks = intent == QueryIntent.UiLocation ? imageChunks : instructionChunks;
        var primaryLabel = intent == QueryIntent.UiLocation
            ? "MÔ TẢ TRỰC QUAN GIAO DIỆN TỪ ẢNH"
            : "VĂN BẢN HƯỚNG DẪN THAO TÁC";
        var secondaryChunks = intent == QueryIntent.UiLocation ? instructionChunks : imageChunks;
        var secondaryLabel = intent == QueryIntent.UiLocation
            ? "VĂN BẢN HƯỚNG DẪN THAO TÁC"
            : "MÔ TẢ TRỰC QUAN GIAO DIỆN TỪ ẢNH";

        // Build primary block
        if (primaryChunks.Count > 0)
        {
            var block = new List<string> { $"=== {primaryLabel} ===" };
            foreach (var (document, metadata) in primaryChunks)
            {
                chunkNumber++;
                block.Add($"[ĐOẠN {chunkNumber} - {BuildSourceLabel(metadata)}]\n{document}");
            }
            sections.Add(string.Join("\n\n", block));
        }

        // Build secondary block
        if (secondaryChunks.Count > 0)
        {
            var block = new List<string> { $"=== {secondaryLabel} ===" };
            foreach (var (document, metadata) in secondaryChunks)
            {
                chunkNumber++;
                block.Add($"[ĐOẠN {chunkNumber} - {BuildSourceLabel(metadata)}]\n{document}");
            }
            sections.Add(string.Join("\n\n", block));
        }

        // Append chunks without content_kind (legacy data)
        if (otherChunks.Count > 0)
        {
            var block = new List<string>();
            foreach (var (document, metadata) in otherChunks)
            {
                chunkNumber++;
                block.Add($"[ĐOẠN {chunkNumber} - {BuildSourceLabel(metadata)}]\n{document}");
            }
            sections.Add(string.Join("\n\n", block));
        }

        return string.Join("\n\n---\n\n", sections);
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
                    : "M\u00ECnh t\u00ECm th\u1EA5y ng\u01B0\u1EDDi n\u00E0y trong t\u00E0i li\u1EC7u nh\u01B0ng ch\u01B0a th\u1EA5y email trong d\u00F2ng d\u1EEF li\u1EC7u.";
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
            answer = "M\u00ECnh ch\u01B0a t\u00ECm th\u1EA5y th\u00F4ng tin n\u00E0y trong t\u00E0i li\u1EC7u \u0111\u00E3 n\u1EA1p.";
            return true;
        }

        return false;
    }

    private static string FormatStudentAnswer(string? studentId, string? fullName, string? email, string? className)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(studentId))
        {
            parts.Add($"M\u00E3 s\u1ED1 ID: {studentId}");
        }

        if (!string.IsNullOrWhiteSpace(fullName))
        {
            parts.Add($"H\u1ECD t\u00EAn: {fullName}");
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            parts.Add($"Email: {email}");
        }

        if (!string.IsNullOrWhiteSpace(className))
        {
            parts.Add($"L\u1EDBp: {className}");
        }

        return parts.Count == 0
            ? "M\u00ECnh ch\u01B0a t\u00ECm th\u1EA5y th\u00F4ng tin n\u00E0y trong t\u00E0i li\u1EC7u \u0111\u00E3 n\u1EA1p."
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
            @"(?:email|mail|th\u01B0 \u0111i\u1EC7n t\u1EED)\s+(?:c\u1EE7a|cua)\s+(.+?)(?:\s+(?:l\u00E0|la)\s+g\u00EC|[?\uFF1F])?$",
            RegexOptions.IgnoreCase);

        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        match = Regex.Match(text, @"(?:c\u1EE7a|cua)\s+(.+?)(?:\s+(?:l\u00E0|la)\s+g\u00EC|[?\uFF1F])?$", RegexOptions.IgnoreCase);
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
                builder.Append(character == '\u0111' ? 'd' : character);
            }
        }

        return string.Join(' ', builder.ToString().Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}




