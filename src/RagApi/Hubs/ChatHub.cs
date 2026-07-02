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
            var requestedTopK = DetermineTopK(effectiveMessage);

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

            var queryAction = ActionKindClassifier.DetermineActionKind(effectiveMessage);
            var isStepActionQuery = queryAction != null && queryIntent == QueryIntent.TaskInstruction;
            if (isStepActionQuery && metadatas.Count > 0)
            {
                var procedureChunks = await _repository.GetProcedureChunksByActionAsync(
                    metadatas,
                    queryAction!,
                    12,
                    cancellationToken);
                AppendRelatedChunks(documents, metadatas, procedureChunks);
            }

            if (queryAction != null)
            {
                var items = documents.Zip(metadatas, (doc, meta) => new { doc, meta }).ToList();
                var hasMatchingProcedure = items.Any(item => ChunkMatchesAction(item.doc, item.meta, queryAction) && IsProcedureSteps(item.meta));
                if (isStepActionQuery && hasMatchingProcedure)
                {
                    items = items
                        .Where(item => !ChunkMatchesDifferentAction(item.doc, item.meta, queryAction))
                        .ToList();
                }

                var sorted = items.OrderBy(item =>
                {
                    if (ChunkMatchesAction(item.doc, item.meta, queryAction) && IsProcedureSteps(item.meta))
                        return 0;
                    if (ChunkMatchesAction(item.doc, item.meta, queryAction))
                        return 1;
                    if (ChunkMatchesDifferentAction(item.doc, item.meta, queryAction))
                        return 4;
                    return 2;
                }).ToList();

                documents = sorted.Select(x => x.doc).ToList();
                metadatas = sorted.Select(x => x.meta).ToList();
            }

            var citations = BuildFrontendCitations(searchResults);
            var contextText = BuildContextText(documents, metadatas, queryIntent);

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



            var systemMessage = "Bạn là trợ lý AI thông minh, chỉ trả lời câu hỏi dựa trên phần NGỮ CẢNH được cung cấp.\n\n" +
                "Luật trả lời:\n" +
                "1. Chỉ sử dụng thông tin trong phần NGỮ CẢNH để trả lời. Không thêm bớt, không tự ý suy diễn, không dùng kiến thức bên ngoài.\n" +
                "2. Nếu ngữ cảnh không có thông tin hoặc không đủ thông tin, bắt buộc phải trả lời đúng: \"Tôi không tìm thấy thông tin này trong tài liệu.\"\n" +
                "3. Đối với câu hỏi yêu cầu các bước thực hiện/hướng dẫn: Chỉ liệt kê các thao tác thật sự theo đúng trình tự từ tài liệu hướng dẫn (ví dụ: Bước 1, Bước 2...). Tuyệt đối không biến nhãn trường, tên cột hoặc các ký tự OCR rác thành các bước thao tác.\n" +
                "4. Bỏ qua các ký tự LaTeX/markup, dòng lặp, hoặc nhãn nhiễu trong ngữ cảnh; không lặp lại cụm từ hoặc dòng quá 2 lần. Dòng có dạng [SLIDE: <title>] là tiêu đề màn hình/tác vụ, dùng để chọn đúng quy trình, tuyệt đối không coi là nhiễu.\n" +
                "5. Trả lời ngắn gọn, trực tiếp, bằng tiếng Việt tự nhiên.";

            var chatHistory = new ChatHistory(systemMessage);
            chatHistory.AddUserMessage($"""
                NGỮ CẢNH:
                {contextText}

                Câu hỏi: {effectiveMessage}
                """);

            await Clients.Caller.SendAsync("ReceiveCitations", citations, cancellationToken);
            await Clients.Caller.SendAsync("ChatStarted", cancellationToken);

            var maxTokens = DetermineMaxTokens(contextText);
            var executionSettings = new OpenAIPromptExecutionSettings
            {
                Temperature = 0,
                TopP = 1,
                FrequencyPenalty = 0.3,
                PresencePenalty = 0,
                MaxTokens = maxTokens,
                StopSequences = ["\nNguồn tài liệu:"]
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
            await Clients.Caller.SendAsync("ReceiveToken", "\n[Lỗi kết nối tới mô hình AI hoặc Database]");
            await Clients.Caller.SendAsync("ChatEnded");
        }
    }

    public static bool IsRepetitionLoop(string text)
    {
        if (text.Length < 100)
        {
            return false;
        }

        return HasRepeatedRecentSentence(text) 
            || HasRepeatedSuffix(text, 40, 2)
            || HasRepeatedLines(text);
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

    private static bool HasRepeatedLines(string text)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => NormalizeText(line).Trim())
            .Where(line => !string.IsNullOrEmpty(line))
            .ToList();

        foreach (var line in lines)
        {
            if (lines.Count(l => l == line) >= 3)
            {
                return true;
            }
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
    public static string CleanContextChunk(string document, Dictionary<string, int> lineOccurrences)
    {
        return RagApi.Services.Ingestion.TextArtifactCleaner.Clean(document, lineOccurrences);
    }

    private static string TruncateToLastSpace(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }
        var truncated = text[..maxLength];
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            truncated = truncated[..lastSpace];
        }
        return truncated.TrimEnd();
    }

    private static List<string> ProcessChunkGroup(
        List<(string document, JsonElement metadata)> inputChunks,
        string? blockHeader,
        ref int chunkNumber,
        Dictionary<string, int> lineOccurrences,
        List<(HashSet<string> LineSet, JsonElement Metadata)> acceptedChunkLineSets,
        List<string> existingSections)
    {
        var block = new List<string>();
        if (!string.IsNullOrEmpty(blockHeader))
        {
            block.Add($"=== {blockHeader} ===");
        }

        foreach (var (doc, meta) in inputChunks)
        {
            var cleaned = CleanContextChunk(doc, lineOccurrences);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            var chunkLimit = IsProcedureSteps(meta) ? 5000 : IsImageUiDescription(meta) ? 1500 : 2000;
            if (cleaned.Length > chunkLimit)
            {
                Console.WriteLine($"Truncated chunk exceeding {chunkLimit} chars: original length = {cleaned.Length}");
                cleaned = TruncateToLastSpace(cleaned, chunkLimit);
            }

            // Kiểm tra trùng lắp gần
            var cleanedLines = cleaned.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)).ToList();
            var normalizedSet = cleanedLines.Select(NormalizeText).ToHashSet();
            bool isNearDuplicate = false;
            if (normalizedSet.Count >= 4)
            {
                foreach (var (prevSet, prevMeta) in acceptedChunkLineSets)
                {
                    if (prevSet.Count < 4) continue;
                    int intersection = normalizedSet.Intersect(prevSet).Count();
                    double overlap = (double)intersection / Math.Max(normalizedSet.Count, prevSet.Count);
                    if (overlap >= 0.85)
                    {
                        var titleA = GetMetadataString(meta, "slide_title");
                        var titleB = GetMetadataString(prevMeta, "slide_title");
                        var actionA = GetMetadataString(meta, "action_kind");
                        var actionB = GetMetadataString(prevMeta, "action_kind");

                        if (titleA == titleB && actionA == actionB)
                        {
                            isNearDuplicate = true;
                            break;
                        }
                    }
                }
            }

            if (isNearDuplicate)
            {
                continue;
            }

            // Kiểm tra xem thêm chunk này có vượt quá 18000 ký tự tổng cộng không
            var tempChunkNumber = chunkNumber + 1;
            var tempBlock = new List<string>(block) { $"[ĐOẠN {tempChunkNumber} - {BuildSourceLabel(meta)}]\n{cleaned}" };
            var tempSections = new List<string>(existingSections) { string.Join("\n\n", tempBlock) };
            var tempContext = string.Join("\n\n---\n\n", tempSections);

            if (tempContext.Length > 18000)
            {
                Console.WriteLine($"Discarded chunk because total context length would exceed 18000 chars.");
                continue;
            }

            // Chấp nhận chunk
            chunkNumber = tempChunkNumber;
            block.Add($"[ĐOẠN {chunkNumber} - {BuildSourceLabel(meta)}]\n{cleaned}");
            acceptedChunkLineSets.Add((normalizedSet, meta));
        }

        return block;
    }

    public static string BuildContextText(IReadOnlyList<string> documents, IReadOnlyList<JsonElement> metadatas, QueryIntent intent)
    {
        // Phân loại documents thành instruction_text và image_ui_description
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

        // Xác định thứ tự ưu tiên của block dựa trên query intent
        var primaryChunks = intent == QueryIntent.UiLocation ? imageChunks : instructionChunks;
        var primaryLabel = intent == QueryIntent.UiLocation
            ? "MO TA TRUC QUAN GIAO DIEN TU ANH"
            : "VAN BAN HUONG DAN THAO TAC";
        var secondaryChunks = intent == QueryIntent.UiLocation ? instructionChunks : imageChunks;
        var secondaryLabel = intent == QueryIntent.UiLocation
            ? "VAN BAN HUONG DAN THAO TAC"
            : "MO TA TRUC QUAN GIAO DIEN TU ANH";

        var lineOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var acceptedChunkLineSets = new List<(HashSet<string> LineSet, JsonElement Metadata)>();

        // Xây dựng primary block
        if (primaryChunks.Count > 0)
        {
            var block = ProcessChunkGroup(primaryChunks, primaryLabel, ref chunkNumber, lineOccurrences, acceptedChunkLineSets, sections);
            if (block.Count > 1) // có header và ít nhất một chunk
            {
                sections.Add(string.Join("\n\n", block));
            }
        }

        // Xây dựng secondary block
        if (secondaryChunks.Count > 0)
        {
            var block = ProcessChunkGroup(secondaryChunks, secondaryLabel, ref chunkNumber, lineOccurrences, acceptedChunkLineSets, sections);
            if (block.Count > 1) // có header và ít nhất một chunk
            {
                sections.Add(string.Join("\n\n", block));
            }
        }

        // Thêm chunks không có content_kind (dữ liệu cũ/legacy)
        if (otherChunks.Count > 0)
        {
            var block = ProcessChunkGroup(otherChunks, null, ref chunkNumber, lineOccurrences, acceptedChunkLineSets, sections);
            if (block.Count > 0) // có ít nhất một chunk (không có header)
            {
                sections.Add(string.Join("\n\n", block));
            }
        }

        return string.Join("\n\n---\n\n", sections);
    }

    public static bool TryAnswerFromStructuredContext(string question, IReadOnlyList<string> documents, out string answer)
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

    private int DetermineTopK(string message)
    {
        int topK;
        if (IsStructuredLookup(message))
        {
            topK = 100;
        }
        else if (HasClearActionIntent(message) || QueryClassifier.Classify(message) == QueryIntent.TaskInstruction)
        {
            topK = 20;
        }
        else
        {
            topK = _searchOptions.ChatTopK;
        }
        return Math.Clamp(topK, 1, Math.Max(1, _searchOptions.TopKMax));
    }

    private bool HasClearActionIntent(string message) => ActionKindClassifier.HasActionIntent(message);

    private static string NormalizeForActionDetection(string text) => ActionKindClassifier.NormalizeText(text);

    private static string? DetermineActionKind(string? title) => ActionKindClassifier.DetermineActionKind(title);

    private static string? GetActionKeyword(string actionKind) => ActionKindClassifier.GetActionKeyword(actionKind);

    private static string? NormalizeActionKind(string? raw) => ActionKindClassifier.NormalizeActionKind(raw);

    private static bool IsProcedureSteps(JsonElement metadata) =>
        string.Equals(GetMetadataString(metadata, "instruction_role"), "procedure_steps", StringComparison.OrdinalIgnoreCase);

    private static bool IsImageUiDescription(JsonElement metadata) =>
        string.Equals(GetMetadataString(metadata, "content_kind"), "image_ui_description", StringComparison.OrdinalIgnoreCase);

    private static int DetermineMaxTokens(string contextText)
    {
        var stepCount = CountProcedureSteps(contextText);
        return stepCount > 0
            ? Math.Clamp(600 + stepCount * 120, 900, 1800)
            : 600;
    }

    private static int CountProcedureSteps(string text) =>
        Regex.Matches(text, @"\b(?:Bước|Buoc|B)\s*\d+\s*:", RegexOptions.IgnoreCase).Count;

    private static bool ContainsActionKeyword(string normalizedLine, string keyword)
    {
        return Regex.IsMatch(
            normalizedLine,
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(keyword)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase);
    }

    private static bool ChunkMatchesAction(string content, JsonElement metadata, string queryAction)
    {
        if (metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("action_kind", out var propAction))
        {
            var val = NormalizeActionKind(propAction.GetString());
            if (!string.IsNullOrEmpty(val))
            {
                return val.Equals(queryAction, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("slide_title", out var propTitle))
        {
            var title = propTitle.GetString();
            var titleAction = DetermineActionKind(title);
            if (titleAction != null)
            {
                return titleAction.Equals(queryAction, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                               .Take(3)
                               .Select(l => NormalizeForActionDetection(l))
                               .ToList();
            
            var keyword = GetActionKeyword(queryAction);
            if (keyword != null)
            {
                foreach (var line in lines)
                {
                    if (ContainsActionKeyword(line, keyword))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool ChunkMatchesDifferentAction(string content, JsonElement metadata, string queryAction)
    {
        string? chunkAction = null;

        if (metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("action_kind", out var propAction))
        {
            chunkAction = NormalizeActionKind(propAction.GetString());
        }

        if (string.IsNullOrEmpty(chunkAction) && metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("slide_title", out var propTitle))
        {
            var title = propTitle.GetString();
            chunkAction = DetermineActionKind(title);
        }

        if (!string.IsNullOrEmpty(chunkAction))
        {
            return !chunkAction.Equals(queryAction, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                               .Take(3)
                               .Select(l => NormalizeForActionDetection(l))
                               .ToList();

            var otherActions = ActionKindClassifier.AllActionKinds
                .Where(a => !a.Equals(queryAction, StringComparison.OrdinalIgnoreCase));

            foreach (var otherAction in otherActions)
            {
                var keyword = GetActionKeyword(otherAction);
                if (keyword != null)
                {
                    foreach (var line in lines)
                    {
                        if (ContainsActionKeyword(line, keyword))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }
}




