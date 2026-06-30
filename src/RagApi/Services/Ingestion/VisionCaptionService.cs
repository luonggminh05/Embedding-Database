using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RagApi.Options;

namespace RagApi.Services.Ingestion;

public sealed class VisionCaptionService : IVisionCaptionService
{
    private readonly HttpClient _httpClient;
    private readonly IngestionOptions _options;
    private readonly ILogger<VisionCaptionService> _logger;
    private static readonly object LimiterLock = new();
    private static SemaphoreSlim? SharedLimiter;
    private static int SharedLimiterSize;

    private readonly SemaphoreSlim _concurrencyLimiter;

    public VisionCaptionService(HttpClient httpClient, IOptions<IngestionOptions> options, ILogger<VisionCaptionService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _concurrencyLimiter = GetSharedLimiter(_options.MaxConcurrentVisionCalls);
    }

    private static SemaphoreSlim GetSharedLimiter(int maxConcurrentCalls)
    {
        lock (LimiterLock)
        {
            if (SharedLimiter == null || SharedLimiterSize != maxConcurrentCalls)
            {
                SharedLimiter = new SemaphoreSlim(maxConcurrentCalls, maxConcurrentCalls);
                SharedLimiterSize = maxConcurrentCalls;
            }

            return SharedLimiter;
        }
    }
    public async Task<string?> GenerateCaptionAsync(
        byte[] imageBytes,
        Dictionary<string, object> metadata,
        string? ocrText,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableVisionCaption || string.IsNullOrWhiteSpace(_options.VisionApiUrl))
        {
            return null;
        }

        if (!ImageUtilities.TryGetDimensions(imageBytes, out var width, out var height))
        {
            _logger.LogWarning("Vision skipped image from {Source}: could not decode dimensions.", GetMetadata(metadata, "source"));
            return null;
        }

        if (width < _options.MinVisionImageWidth || height < _options.MinVisionImageHeight)
        {
            _logger.LogDebug(
                "Vision skipped small image from {Source}: {Width}x{Height}px.",
                GetMetadata(metadata, "source"),
                width,
                height);
            return null;
        }

        var compressedBytes = ImageUtilities.CompressForVision(imageBytes);
        var imageBase64 = Convert.ToBase64String(compressedBytes);
        var prompt = BuildPrompt(ocrText);

        var payload = new
        {
            model = _options.VisionModel,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:image/jpeg;base64,{imageBase64}"
                            }
                        }
                    }
                }
            },
            temperature = 0,
            max_tokens = 1600
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.VisionTimeoutSeconds));

        try
        {
            await _concurrencyLimiter.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Vision API call timed out while waiting for concurrency slot.");
            return null;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _options.VisionApiUrl)
            {
                Content = JsonContent.Create(payload, options: new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                })
            };

            if (_options.VisionApiKey != "EMPTY")
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.VisionApiKey);
            }

            var response = await _httpClient.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(responseJson);
            
            var caption = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(caption)) return null;

            return caption.Trim();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Vision API call timed out for image from {Source}", GetMetadata(metadata, "source"));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vision API error for image from {Source}", GetMetadata(metadata, "source"));
            return null;
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private static string BuildPrompt(string? ocrText)
    {
        var prompt = """
            <image>
            You are a precise vision extraction engine for a RAG chatbot. The image may be a business software screen, search form, data table, email, document, notification, or screenshot.

            Core rules:
            - Read the image directly and describe the UI structure in detail.
            - Use OCR text as supporting evidence to verify exact label, table, tab, menu, and button text. OCR may be incomplete or garbled.
            - If OCR conflicts with clearly visible text in the image, use the clearly visible image text. If neither is clear, write "unknown".
            - Do not invent hidden fields, values, rows, senders, recipients, dates, attachments, organizations, business topics, or actions.
            - Do not output random numbers, repeated numeric sequences, filler text, examples, or unrelated world knowledge. If you cannot read the image, return UNKNOWN sections with unknown values.
            - Never infer document metadata such as nguoi lap, ngay lap, file dinh kem, don vi, or loai nghiep vu unless that exact information is visible in the image or OCR text.
            - Describe UI layout spatially: left sidebar, top toolbar, breadcrumb/tab row, main content, upper panel, lower panel, left column, right column. Use relative positions, not pixel coordinates.
            - For every visible label, field, table, panel, tab, menu item, and button, include where it appears and what visible content belongs to it.
            - Extract only visible information as database-ready text so a chatbot can later answer questions about fields, counts, columns, buttons, required inputs, menu paths, tabs, panels, label positions, and email contents.
            - If a value is not visible, write "unknown". If a section is not applicable, write "not applicable".
            - If a field label has a visible "*" or another required marker, set Required: yes and mention that the field is mandatory.
            - Keep section titles exactly as shown below in ASCII. Preserve Vietnamese text from the image exactly as visible.
            - Keep fixed section titles in ASCII, but write extracted descriptions, expected inputs, QUESTION_ANSWER_HINTS, and SEARCHABLE_SUMMARY in Vietnamese when the image contains Vietnamese text.

            Return the answer in this exact structure:

            IMAGE_TYPE:
            One of UI_SCREEN, EMAIL, DOCUMENT, TABLE, FORM, UNKNOWN.

            SYSTEM_OR_APP_NAME:
            Visible system, app, organization, or product name.

            SCREEN_OR_DOCUMENT_TITLE:
            Current screen name, email subject, document title, or main visible title.

            VISIBLE_TEXT:
            Important visible text lines copied from the image. Prefer exact Vietnamese labels verified by OCR when available.

            NAVIGATION_AND_TABS:
            - Active sidebar menu item: visible active menu/module, or unknown.
            - Other visible sidebar menu items: list visible names.
            - Breadcrumb path: visible breadcrumb/tab path from left to right, or unknown.
            - Active tab/page: visible active tab/page name, or unknown.
            - Top toolbar actions from left to right: visible actions.

            UI_LAYOUT:
            Describe the screen layout in Vietnamese: where the sidebar, toolbar, breadcrumb/tabs, main panels, search form, and data table are positioned.

            UI_PANELS:
            For each visible panel/block/card/section:
            - Panel name: visible panel title
              Location: relative position on screen, such as upper main content, lower main content, left sidebar, top toolbar
              Panel purpose: what the panel is for
              Field count: number of visible input/select/filter fields in this panel
              Fields:
              - Field name: visible field label
                Location: relative position inside the panel, such as left column row 1, right column row 2
                Control type: textbox | dropdown | date-picker | checkbox | radio | textarea | button | table-column | label | unknown
                Expected input: what the user should type or choose in this field
                Current value or placeholder: visible value, default option, or placeholder
                Required: yes | no | unknown. Use yes when the label has "*" or another required marker.
                Notes: useful visible details only
              Buttons/actions:
              - Button/action name: visible button or action name
                Location: relative position, such as top toolbar, upper-right of main content, inside panel footer

            SEARCH_CONDITIONS:
            If the image has a search/filter panel such as "Dieu kien tim kiem", "Tim kiem", "Bo loc", or similar search/filter text, fill this section carefully.
            - Search panel name: visible name of the search/filter panel
            - Search panel location: relative position on screen
            - Total search fields: exact count of visible search/filter fields
            - Search fields:
              - Field name: visible label
                Location: relative position inside the search panel
                Control type: textbox | dropdown | date-picker | checkbox | radio | unknown
                Expected input: what content should be entered or selected
                Current value or placeholder: visible value/default/placeholder
            If there is no search/filter panel, write Total search fields: 0.

            DATA_TABLES:
            For each visible data table:
            - Table name: visible table title
              Location: relative position on screen
              Column count: number of visible columns
              Columns from left to right:
              - Column name: visible column name
                Location/order: column order from left to right
              Rows/data:
              - visible row data, or "No data" if the table says there is no data.

            EMAIL_INFO:
            Fill only if IMAGE_TYPE is EMAIL.
            - From: visible sender
            - To: visible recipients
            - Cc: visible cc recipients
            - Subject: visible subject
            - Date/time: visible date or time
            - Main content: concise but complete email body extraction
            - Requested actions: tasks, requests, deadlines, approvals, replies, or follow-ups mentioned
            - Attachments: visible attachment names

            BUTTONS_OR_ACTIONS:
            - Button/action/menu item name: visible name, including toolbar buttons and export/import actions
              Location: where it appears

            COUNTS:
            - Number of visible panels: count
            - Number of search condition fields: count
            - Number of form fields: count
            - Number of data tables: count
            - Number of visible table columns: count
            - Number of buttons/actions: count

            QUESTION_ANSWER_HINTS:
            Write direct Vietnamese answer sentences with diacritics when possible. These sentences will be stored for RAG retrieval. Only use facts visible in the image or supported by OCR:
            - Man hinh/module dang mo la <active menu/page>; duong dan/tab hien thi la <breadcrumb/tab path>.
            - Bang dieu kien tim kiem nam o <location> va co <n> o/truong gom: <field names>.
            - Cac noi dung can nhap/chon trong dieu kien tim kiem la: <expected inputs for each field>.
            - Bang du lieu <table name> nam o <location> va co cac cot tu trai sang phai: <column names>.
            - Cac nut thao tac tren man hinh gom: <button/action names>, nam tai <locations>.
            - Neu la email: Email nay noi ve <topic>; yeu cau/hanh dong can lam la <actions>.

            SEARCHABLE_SUMMARY:
            Write 4-8 natural Vietnamese sentences for semantic search. Include only visible screen/email/document title, active sidebar menu, breadcrumb/tab path, panel names and locations, search fields with positions, expected inputs, table columns, buttons/actions with locations, and key email requests when applicable. Do not add fields or values that are not visible.
            """;
        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            prompt += $"\nExisting OCR text, may be incomplete or garbled. Use it to verify exact label/table/button text. Still read the image directly for layout, positions, panels, tabs, and table structure. Do not invent facts that are not visible: {ocrText[..Math.Min(ocrText.Length, 3000)]}";
        }

        return prompt;
    }

    private static object? GetMetadata(Dictionary<string, object> metadata, string key) =>
        metadata.TryGetValue(key, out var value) ? value : null;
}


