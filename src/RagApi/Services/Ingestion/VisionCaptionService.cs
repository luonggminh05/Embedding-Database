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

    public VisionCaptionService(HttpClient httpClient, IOptions<IngestionOptions> options, ILogger<VisionCaptionService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
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
            max_tokens = 1800
        };

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.VisionTimeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.VisionApiUrl)
            {
                Content = JsonContent.Create(payload)
            };

            if (!string.IsNullOrWhiteSpace(_options.VisionApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.VisionApiKey);
            }

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Vision failed for {Source}: HTTP {StatusCode}.", GetMetadata(metadata, "source"), response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
            var caption = json.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
                ?.Trim();

            if (!string.IsNullOrWhiteSpace(caption))
            {
                _logger.LogInformation("Vision caption generated for {Source} ({Width}x{Height}px).", GetMetadata(metadata, "source"), width, height);
                return caption;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Vision timed out for {Source} after {TimeoutSeconds}s.", GetMetadata(metadata, "source"), _options.VisionTimeoutSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vision failed for {Source}.", GetMetadata(metadata, "source"));
        }

        return null;
    }

    private static string BuildPrompt(string? ocrText)
    {
        var prompt = """
            <image>
            You are a precise vision extraction engine for a RAG chatbot. The image may be a business software screen, search form, data table, email, document, notification, or screenshot.

            Core rules:
            - Read all visible text exactly, especially Vietnamese UI labels and table headers.
            - Do not give generic descriptions such as "this is a software screen".
            - Do not invent hidden fields, values, rows, senders, recipients, or actions.
            - Extract information as database-ready text so a chatbot can later answer questions about fields, counts, columns, buttons, required inputs, and email contents.
            - If a value is not visible, write "unknown". If a section is not applicable, write "not applicable".
            - If a field label has a visible "*" or another required marker, set Required: yes and mention that the field is mandatory.
            - Keep section titles exactly as shown below in ASCII. Preserve Vietnamese text from the image exactly as visible.
            - Keep the fixed section titles in ASCII, but write extracted descriptions, expected inputs, QUESTION_ANSWER_HINTS, and SEARCHABLE_SUMMARY in Vietnamese when the image contains Vietnamese text.

            Return the answer in this exact structure:

            IMAGE_TYPE:
            One of UI_SCREEN, EMAIL, DOCUMENT, TABLE, FORM, UNKNOWN.

            SYSTEM_OR_APP_NAME:
            Visible system, app, organization, or product name.

            SCREEN_OR_DOCUMENT_TITLE:
            Current screen name, email subject, document title, or main visible title.

            VISIBLE_TEXT:
            Important visible text lines copied from the image.

            UI_PANELS:
            For each visible panel/block/card/section:
            - Panel name: visible panel title
              Panel purpose: what the panel is for
              Field count: number of visible input/select/filter fields in this panel
              Fields:
              - Field name: visible field label
                Control type: textbox | dropdown | date-picker | checkbox | radio | textarea | button | table-column | label | unknown
                Expected input: what the user should type or choose in this field
                Current value or placeholder: visible value, default option, or placeholder
                Required: yes | no | unknown. Use yes when the label has "*" or another required marker.
                Notes: useful visible details only
              Buttons/actions:
              - visible button or action name

            SEARCH_CONDITIONS:
            If the image has a search/filter panel such as "Dieu kien tim kiem", "Tim kiem", "Bo loc", or similar search/filter text, fill this section carefully.
            - Search panel name: visible name of the search/filter panel
            - Total search fields: exact count of visible search/filter fields
            - Search fields:
              - Field name: visible label
                Control type: textbox | dropdown | date-picker | checkbox | radio | unknown
                Expected input: what content should be entered or selected
                Current value or placeholder: visible value/default/placeholder
            If there is no search/filter panel, write Total search fields: 0.

            DATA_TABLES:
            For each visible data table:
            - Table name: visible table title
              Column count: number of visible columns
              Columns:
              - visible column name
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
            - visible button/action/menu item names, including toolbar buttons and export/import actions.

            COUNTS:
            - Number of visible panels: count
            - Number of search condition fields: count
            - Number of form fields: count
            - Number of data tables: count
            - Number of visible table columns: count
            - Number of buttons/actions: count

            QUESTION_ANSWER_HINTS:
            Write direct Vietnamese answer sentences with diacritics when possible. These sentences will be stored for RAG retrieval:
            - Bang dieu kien tim kiem co <n> o/truong gom: <field names>.
            - Cac noi dung can nhap/chon trong dieu kien tim kiem la: <expected inputs for each field>.
            - Bang du lieu co cac cot: <column names>.
            - Cac nut thao tac tren man hinh gom: <button/action names>.
            - Neu la email: Email nay noi ve <topic>; yeu cau/hanh dong can lam la <actions>.

            SEARCHABLE_SUMMARY:
            Write 3-6 natural Vietnamese sentences for semantic search. Include the screen/email/document title, purpose, search fields, expected inputs, table columns, buttons/actions, and key email requests when applicable.
            """;

        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            prompt += $"\nExisting OCR text, use it to correct and enrich the extraction: {ocrText[..Math.Min(ocrText.Length, 2000)]}";
        }

        return prompt;
    }

    private static object? GetMetadata(Dictionary<string, object> metadata, string key) =>
        metadata.TryGetValue(key, out var value) ? value : null;
}
