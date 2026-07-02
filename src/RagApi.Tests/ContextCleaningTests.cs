using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using RagApi.Hubs;
using RagApi.Models;
using RagApi.Services;
using RagApi.Options;
using RagApi.Services.Ingestion;
using RagApi.Services.Ingestion.Parsers;
using SkiaSharp;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RagApi.Tests
{
    public class ContextCleaningTests
    {
        [Fact]
        public void CleanContextChunk_RemovesLatexArtifacts()
        {
            var lineOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var document = @"\[ \begin{array}{l}
Bước 1: Chọn Đăng ký học phần
\end{array} \]";
            
            var result = ChatHub.CleanContextChunk(document, lineOccurrences);
            
            Assert.Contains("Bước 1: Chọn Đăng ký học phần", result);
            Assert.DoesNotContain("\\begin", result);
            Assert.DoesNotContain("\\end", result);
            Assert.DoesNotContain("\\[", result);
            Assert.DoesNotContain("\\]", result);
        }

        [Fact]
        public void CleanContextChunk_KeepsValidShortActions()
        {
            var lineOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var document = @"Bước 3
Lưu lại
Nhấn Lưu";
            
            var result = ChatHub.CleanContextChunk(document, lineOccurrences);
            
            Assert.Contains("Bước 3", result);
            Assert.Contains("Lưu lại", result);
            Assert.Contains("Nhấn Lưu", result);
        }

        [Fact]
        public void CleanContextChunk_LimitsIdenticalLinesToMax2()
        {
            var lineOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var chunk1 = "File đính kèm: Người tạo phiếu\nFile đính kèm: Người tạo phiếu";
            var chunk2 = "File đính kèm: Người tạo phiếu\nFile đính kèm: Người tạo phiếu";
            
            var result1 = ChatHub.CleanContextChunk(chunk1, lineOccurrences);
            var result2 = ChatHub.CleanContextChunk(chunk2, lineOccurrences);
            
            // First chunk should keep both because limit is 2
            Assert.Contains("File đính kèm: Người tạo phiếu", result1);
            var lines1 = result1.Split('\n');
            Assert.Equal(2, lines1.Length);
            
            // Second chunk should have none because the line occurrences already reached 2
            Assert.Empty(result2);
        }

        [Fact]
        public void CleanContextChunk_DoesNotDeduplicateDifferentValuesWithSameStructure()
        {
            var lineOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var document = @"Lớp: A
Lớp: B
Lớp: C";
            
            var result = ChatHub.CleanContextChunk(document, lineOccurrences);
            
            Assert.Contains("Lớp: A", result);
            Assert.Contains("Lớp: B", result);
            Assert.Contains("Lớp: C", result);
        }

        [Fact]
        public void TryAnswerFromStructuredContext_UsesOriginalDocumentsSuccessfully()
        {
            var documents = new List<string>
            {
                "MSSV: 20110123, Họ tên: Nguyễn Văn A, Email: a.nguyen@hcmut.edu.vn, Lớp: L01",
                "MSSV: 20110456, Họ tên: Trần Thị B, Email: b.tran@hcmut.edu.vn, Lớp: L02"
            };

            // Test safety: TryAnswerFromStructuredContext must successfully query student
            bool found = ChatHub.TryAnswerFromStructuredContext("Thông tin của sinh viên có mssv 20110123", documents, out var answer);
            
            Assert.True(found);
            Assert.Contains("Nguyễn Văn A", answer);
            Assert.Contains("20110123", answer);
            Assert.Contains("L01", answer);
        }

        [Fact]
        public void BuildContextText_ChecksNearDuplicateCorrectly()
        {
            // Set up mock metadata
            var meta1 = JsonSerializer.Deserialize<JsonElement>("{\"content_kind\":\"instruction_text\",\"source\":\"doc1.pdf\",\"page\":\"1\"}");
            var meta2 = JsonSerializer.Deserialize<JsonElement>("{\"content_kind\":\"instruction_text\",\"source\":\"doc1.pdf\",\"page\":\"2\"}");
            
            var metadatas = new List<JsonElement> { meta1, meta2 };

            // 1. Chunk 3 lines or less are exempt from overlap-check
            var doc3Lines1 = "Dòng 1\nDòng 2\nDòng 3";
            var doc3Lines2 = "Dòng 3\nDòng 2\nDòng 1"; // Different document content order, same set
            
            var context3Lines = ChatHub.BuildContextText(
                new List<string> { doc3Lines1, doc3Lines2 }, 
                metadatas, 
                QueryIntent.TaskInstruction
            );
            
            // Both chunks should be present because they are 3 lines or less (overlap check exempt)
            Assert.Contains("ĐOẠN 1", context3Lines);
            Assert.Contains("ĐOẠN 2", context3Lines);

            // 2. Chunk with exactly 4 lines or more should trigger overlap-check
            var doc4Lines1 = "Dòng 1\nDòng 2\nDòng 3\nDòng 4";
            var doc4Lines2 = "Dòng 4\nDòng 3\nDòng 2\nDòng 1"; // Different document content order, same set
            
            var context4Lines = ChatHub.BuildContextText(
                new List<string> { doc4Lines1, doc4Lines2 }, 
                metadatas, 
                QueryIntent.TaskInstruction
            );
            
            // Second chunk should be rejected as a near-duplicate
            Assert.Contains("ĐOẠN 1", context4Lines);
            Assert.DoesNotContain("ĐOẠN 2", context4Lines);

            // 3. Near-duplicate chunks with >= 85% overlap are rejected
            var doc7Lines1 = "Dòng 1\nDòng 2\nDòng 3\nDòng 4\nDòng 5\nDòng 6\nDòng 7";
            var doc7Lines2 = "Dòng 1\nDòng 2\nDòng 3\nDòng 4\nDòng 5\nDòng 6\nDòng Tám"; // 6 out of 7 lines match (85.7% overlap) -> Rejected
            
            var contextNearDup = ChatHub.BuildContextText(
                new List<string> { doc7Lines1, doc7Lines2 }, 
                metadatas, 
                QueryIntent.TaskInstruction
            );
            Assert.Contains("ĐOẠN 1", contextNearDup);
            Assert.DoesNotContain("ĐOẠN 2", contextNearDup);
        }

        [Fact]
        public void IsRepetitionLoop_DetectsRepetitionsCorrectly()
        {
            // 1. Suffix repeat: 40 character suffix repeated twice
            var repeatedSuffixText = "Đây là một văn bản mẫu dùng để kiểm tra việc lặp câu từ lặp câu từ lặp câu từ lặp câu từ lặp câu từ lặp câu từ lặp câu từ lặp câu từ";
            Assert.True(ChatHub.IsRepetitionLoop(repeatedSuffixText));

            // 2. Line repeat: A line occurring 3 times or more (total text length >= 100)
            var repeatedLineText = "Bắt đầu quá trình lưu trữ thông tin hệ thống.\nNhấn nút Lưu để tiếp tục.\nNhấn nút Lưu để tiếp tục.\nNhấn nút Lưu để tiếp tục.";
            Assert.True(ChatHub.IsRepetitionLoop(repeatedLineText));

            // 3. Do not stop valid multi-step instructions
            var validSteps = @"Để thực hiện tạo đề xuất mới, thực hiện các bước sau:
Bước 1: Chọn chức năng Thêm mới trên thanh công cụ.
Bước 2: Nhập đầy đủ thông tin yêu cầu của đề xuất.
Bước 3: Chọn nút Nhấn để lưu lại thông tin vừa nhập.
Bước 4: Nhấn Chọn người duyệt đề xuất từ danh sách.
Bước 5: Nhấn Lưu để hoàn tất quy trình gửi đề xuất.";
            
            Assert.False(ChatHub.IsRepetitionLoop(validSteps));

            // 4. Do not stop valid field lists
            var validFields = @"Người tạo: Nguyễn Văn A
Ngày tạo: 30/06/2026
Đơn vị: Phòng Đào tạo
Trạng thái: Đang soạn thảo";
            
            Assert.False(ChatHub.IsRepetitionLoop(validFields));
        }

        [Fact]
        public void TextSplitter_ChunkCountIsProportional()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 833; i++)
            {
                sb.Append("abcde ");
            }
            var text = sb.ToString();
            var doc = new IngestedDocument { PageContent = text, Metadata = new Dictionary<string, object>() };
            
            var result = TextSplitter.SplitDocuments(new List<IngestedDocument> { doc }, 600, 100);
            
            Assert.InRange(result.Count, 8, 12);
        }

        [Fact]
        public void TextSplitter_MinAdvanceGuardPreventsInfiniteLoopOrSlowProgress()
        {
            var text = "a b c d e f g h i j k l m n o p q r s t u v w x y z";
            var doc = new IngestedDocument { PageContent = text, Metadata = new Dictionary<string, object>() };
            
            var result = TextSplitter.SplitDocuments(new List<IngestedDocument> { doc }, 10, 4);
            
            Assert.NotEmpty(result);
            Assert.True(result.Count < 50);
        }

        [Fact]
        public void TextSplitter_ZeroOverlapCase()
        {
            var text = "Dòng 1. Dòng 2. Dòng 3. Dòng 4. Dòng 5. Dòng 6.";
            var doc = new IngestedDocument { PageContent = text, Metadata = new Dictionary<string, object>() };
            
            var result = TextSplitter.SplitDocuments(new List<IngestedDocument> { doc }, 15, 0);
            
            Assert.NotEmpty(result);
            var combined = string.Join(" ", result.Select(r => r.PageContent));
            Assert.Contains("Dòng 1", combined);
            Assert.Contains("Dòng 6", combined);
        }

        [Fact]
        public void TextArtifactCleaner_CleansMixedTextCorrectly()
        {
            var text = @"\[ \begin{array}{l}
Mục lục chương trình đào tạo
\end{array} \]
File đính kèm: Người tạo phiếu
File đính kèm: Người tạo phiếu
File đính kèm: Người tạo phiếu
---
Lớp: L01
Lớp: L02
Bước 1
Lưu";
            
            var result = TextArtifactCleaner.Clean(text);
            
            Assert.Contains("Mục lục chương trình đào tạo", result);
            Assert.DoesNotContain("\\begin", result);
            Assert.DoesNotContain("\\end", result);
            Assert.DoesNotContain("\\[", result);
            Assert.DoesNotContain("\\]", result);
            
            var occurrences = result.Split('\n').Count(l => l.Contains("File đính kèm: Người tạo phiếu"));
            Assert.Equal(2, occurrences);
            
            Assert.DoesNotContain("---", result.Split('\n'));
            
            Assert.Contains("Bước 1", result);
            Assert.Contains("Lưu", result);
            
            Assert.Contains("Lớp: L01", result);
            Assert.Contains("Lớp: L02", result);
        }

        [Fact]
        public async Task WaitForFileAccessAsync_ReturnsTrueAfterStableMetadata()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(tempFile, "hello");
                
                var options = Microsoft.Extensions.Options.Options.Create(new IngestionOptions());
                var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentIngestionWorker>.Instance;
                var worker = new DocumentIngestionWorker(null!, options, logger);

                var cts = new CancellationTokenSource();
                var accessTask = worker.WaitForFileAccessAsync(tempFile, cts.Token);

                await Task.Delay(300);
                await File.WriteAllTextAsync(tempFile, "hello world stability test");

                var result = await accessTask;
                Assert.True(result);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Fact]
        public void IngestionOptions_OverlapGreaterThanHalfChunkSize_Throws()
        {
            var options = new IngestionOptions
            {
                ChunkSize = 600,
                ChunkOverlap = 301
            };
            Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        }

        [Fact]
        public void TextSplitter_OverlapGreaterThanHalfChunkSize_Throws()
        {
            var doc = new IngestedDocument { PageContent = "hello", Metadata = new Dictionary<string, object>() };
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TextSplitter.SplitDocuments(new List<IngestedDocument> { doc }, 600, 301)
            );
        }

        [Fact]
        public void TextSplitter_InvalidChunkSize_Throws()
        {
            var doc = new IngestedDocument { PageContent = "hello", Metadata = new Dictionary<string, object>() };
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TextSplitter.SplitDocuments(new List<IngestedDocument> { doc }, 0, 100)
            );
        }

        [Fact]
        public void TextSplitter_InvalidOverlap_Throws()
        {
            var doc = new IngestedDocument { PageContent = "hello", Metadata = new Dictionary<string, object>() };
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TextSplitter.SplitDocuments(new List<IngestedDocument> { doc }, 600, -1)
            );
        }

        [Fact]
        public void TextSplitter_WorstCaseWhitespace_DoesNotExplodeChunkCount()
        {
            var text = "hello" + new string(' ', 1000) + "world";
            var doc = new IngestedDocument { PageContent = text, Metadata = new Dictionary<string, object>() };
            
            var result = TextSplitter.SplitDocuments(new List<IngestedDocument> { doc }, 100, 20);
            
            Assert.True(result.Count <= 15);
        }

        [Fact]
        public void BuildContextText_DoesNotExceedContextLimit()
        {
            var documents = new List<string>();
            var metadatas = new List<JsonElement>();
            
            for (int i = 0; i < 20; i++)
            {
                documents.Add($"Dòng nội dung thứ {i} dài ra để test limit. " + new string('a', 900));
                metadatas.Add(JsonSerializer.Deserialize<JsonElement>("{\"content_kind\":\"instruction_text\",\"source\":\"doc.pdf\",\"page\":\"1\"}"));
            }
            
            var context = ChatHub.BuildContextText(documents, metadatas, QueryIntent.TaskInstruction);
            
            Assert.True(context.Length <= 18000);
        }

        [Fact]
        public void BuildContextText_DiscardsWholeChunkWhenContextLimitExceeded()
        {
            var documents = new List<string>();
            var metadatas = new List<JsonElement>();
            
            for (int i = 1; i <= 12; i++)
            {
                documents.Add($"ChunkNumber{i} " + new string((char)('a' + i), 1950));
                metadatas.Add(JsonSerializer.Deserialize<JsonElement>($"{{\"content_kind\":\"instruction_text\",\"source\":\"doc.pdf\",\"page\":\"{i}\"}}"));
            }
            
            var context = ChatHub.BuildContextText(documents, metadatas, QueryIntent.TaskInstruction);
            
            Assert.Contains("ChunkNumber1", context);
            
            int lastFound = -1;
            for (int i = 1; i <= 12; i++)
            {
                if (context.Contains($"ChunkNumber{i}"))
                {
                    lastFound = i;
                }
            }
            
            Assert.True(lastFound > 0);
            for (int i = lastFound + 1; i <= 12; i++)
            {
                Assert.DoesNotContain($"ChunkNumber{i}", context);
            }
        }

        [Fact]
        public void TextSplitter_SpaceCutTooEarly_DoesNotLoseData()
        {
            var text = "a" + new string('b', 10) + " " + new string('c', 8); // space at index 11
            var doc = new IngestedDocument { PageContent = text, Metadata = new Dictionary<string, object>() };
            
            var result = TextSplitter.SplitDocuments(new List<IngestedDocument> { doc }, 20, 10);
            
            var combined = string.Join(" ", result.Select(r => r.PageContent));
            Assert.Contains("a", combined);
            Assert.Contains("c", combined);
        }

        [Fact]
        public void ImageContentClassifier_BuildInlineMarker_ReturnsExpected()
        {
            Assert.Null(ImageContentClassifier.BuildInlineMarker(null));
            Assert.Null(ImageContentClassifier.BuildInlineMarker(""));
            Assert.Null(ImageContentClassifier.BuildInlineMarker("   "));
            Assert.Equal("[N\u00FAt/\u1EA2nh: Save]", ImageContentClassifier.BuildInlineMarker("Save"));
        }

        [Fact]
        public void ImageUtilities_PrepareOcrVariants_ReturnsFourVariants()
        {
            using var bitmap = new SKBitmap(10, 10);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Red);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var rawBytes = data.ToArray();

            var variants = ImageUtilities.PrepareOcrVariants(rawBytes);

            Assert.Equal(4, variants.Count);
            
            using var baseline = SKBitmap.Decode(variants[0]);
            using var padded = SKBitmap.Decode(variants[1]);
            Assert.True(padded.Width > baseline.Width);
            Assert.True(padded.Height > baseline.Height);
        }

        [Fact]
        public void SlideAction_SlideTitleAndActionDetection_ReturnsExpected()
        {
            var determineMethod = typeof(PowerPointParser).GetMethod("DetermineActionKind", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            
            Assert.NotNull(determineMethod);
            
            var action1 = determineMethod.Invoke(null, new object[] { "T\u1edd tr\u00ecnh nghi\u1ec7p v\u1ee5 - Ph\u00ea duy\u1ec7t" }) as string;
            var action1B = determineMethod.Invoke(null, new object[] { "T\u1edd tr\u00ecnh nghi\u1ec7p v\u1ee5 - Duy\u1ec7t" }) as string;
            var action2 = determineMethod.Invoke(null, new object[] { "T\u1edd tr\u00ecnh nghi\u1ec7p v\u1ee5 - \u1ee6y quy\u1ec1n" }) as string;
            var action3 = determineMethod.Invoke(null, new object[] { "T\u1edd tr\u00ecnh nghi\u1ec7p v\u1ee5 - In t\u1edd tr\u00ecnh" }) as string;
            
            Assert.Equal("duyet", action1);
            Assert.Equal("duyet", action1B);
            Assert.Equal("uy_quyen", action2);
            Assert.Equal("in", action3);
        }

        [Fact]
        public void ChatHub_ReranksChunks_CorrectlyPrioritizesAction()
        {
            var documents = new List<string>
            {
                "N\u1ed9i dung slide in \u1ea5n",
                "N\u1ed9i dung slide duy\u1ec7t",
                "N\u1ed9i dung slide kh\u00f4ng c\u00f3 action"
            };

            var meta1 = JsonSerializer.Deserialize<JsonElement>("{\"content_kind\":\"instruction_text\",\"action_kind\":\"in\"}");
            var meta2 = JsonSerializer.Deserialize<JsonElement>("{\"content_kind\":\"instruction_text\",\"action_kind\":\"duyet\"}");
            var meta3 = JsonSerializer.Deserialize<JsonElement>("{\"content_kind\":\"instruction_text\"}");
            
            var metadatas = new List<JsonElement> { meta1, meta2, meta3 };

            var chunkMatchesAction = typeof(ChatHub).GetMethod("ChunkMatchesAction", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var chunkMatchesDiff = typeof(ChatHub).GetMethod("ChunkMatchesDifferentAction", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            Assert.NotNull(chunkMatchesAction);
            Assert.NotNull(chunkMatchesDiff);

            var isMatch1 = (bool)chunkMatchesAction.Invoke(null, new object[] { documents[0], meta1, "duyet" })!;
            var isMatch2 = (bool)chunkMatchesAction.Invoke(null, new object[] { documents[1], meta2, "duyet" })!;
            var isMatch3 = (bool)chunkMatchesAction.Invoke(null, new object[] { documents[2], meta3, "duyet" })!;

            Assert.False(isMatch1);
            Assert.True(isMatch2);
            Assert.False(isMatch3);

            var isDiff1 = (bool)chunkMatchesDiff.Invoke(null, new object[] { documents[0], meta1, "duyet" })!;
            var isDiff2 = (bool)chunkMatchesDiff.Invoke(null, new object[] { documents[1], meta2, "duyet" })!;
            var isDiff3 = (bool)chunkMatchesDiff.Invoke(null, new object[] { documents[2], meta3, "duyet" })!;

            Assert.True(isDiff1);
            Assert.False(isDiff2);
            Assert.False(isDiff3);

            // Test backward compatibility: metadata.action_kind = "phe_duyet" matches queryAction = "duyet"
            var metaOld = JsonSerializer.Deserialize<JsonElement>("{\"content_kind\":\"instruction_text\",\"action_kind\":\"phe_duyet\"}");
            var isMatchOld = (bool)chunkMatchesAction.Invoke(null, new object[] { "Nội dung", metaOld, "duyet" })!;
            var isDiffOld = (bool)chunkMatchesDiff.Invoke(null, new object[] { "Nội dung", metaOld, "duyet" })!;

            Assert.True(isMatchOld);
            Assert.False(isDiffOld);

            var isDiffOldOther = (bool)chunkMatchesDiff.Invoke(null, new object[] { "Nội dung", metaOld, "uy_quyen" })!;
            Assert.True(isDiffOldOther);
        }

        [Fact]
        public void ChatHub_ActionKeywordMatching_EnforcesWordBoundaries()
        {
            var chunkMatchesAction = typeof(ChatHub).GetMethod("ChunkMatchesAction", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var chunkMatchesDiff = typeof(ChatHub).GetMethod("ChunkMatchesDifferentAction", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            Assert.NotNull(chunkMatchesAction);
            Assert.NotNull(chunkMatchesDiff);

            // Document has "to trinh" which has "in" as a substring. It should NOT match action "in"
            var docSubstring = "tờ trình nghiệp vụ\nxử lý tờ trình";
            var metaEmpty = JsonSerializer.Deserialize<JsonElement>("{}");

            // For queryAction = "in": "in" is not in boundaries of "tờ trình nghiệp vụ", so it shouldn't match "in"
            var isMatchSubstring = (bool)chunkMatchesAction!.Invoke(null, new object[] { docSubstring, metaEmpty, "in" })!;
            Assert.False(isMatchSubstring);

            // For queryAction = "tim_kiem": action "in" is not matched in "tờ trình", so it shouldn't be considered a different action
            var isDiffSubstring = (bool)chunkMatchesDiff!.Invoke(null, new object[] { docSubstring, metaEmpty, "tim_kiem" })!;
            Assert.False(isDiffSubstring);

            // Document contains "in" as a separate word ("in tờ trình")
            var docWord = "in tờ trình nghiệp vụ";
            // For queryAction = "in": it should match
            var isMatchWord = (bool)chunkMatchesAction.Invoke(null, new object[] { docWord, metaEmpty, "in" })!;
            Assert.True(isMatchWord);

            // For queryAction = "tim_kiem": because it matches "in", it should be considered a different action
            var isDiffWord = (bool)chunkMatchesDiff.Invoke(null, new object[] { docWord, metaEmpty, "tim_kiem" })!;
            Assert.True(isDiffWord);
        }

        [Fact]
        public void ChatHub_BuildContextText_KeepsSimilarChunksWithDifferentSlides()
        {
            var docText = "D\u00f2ng h\u01b0\u1edbng d\u1eabn thao t\u00e1c 1\nD\u00f2ng h\u01b0\u1edbng d\u1eabn thao t\u00e1c 2\nD\u00f2ng h\u01b0\u1edbng d\u1eabn thao t\u00e1c 3\nD\u00f2ng h\u01b0\u1edbng d\u1eabn thao t\u00e1c 4";
            
            var doc1 = "[SLIDE: Slide A]\n" + docText;
            var doc2 = "[SLIDE: Slide B]\n" + docText;
            var doc3 = "[SLIDE: Slide C]\n" + docText;
            
            var docs = new List<string> { doc1, doc2, doc3 };
            
            var meta1 = JsonSerializer.Deserialize<JsonElement>("{\"content_kind\":\"instruction_text\",\"slide_title\":\"Slide A\"}");
            var meta2 = JsonSerializer.Deserialize<JsonElement>("{\"content_kind\":\"instruction_text\",\"slide_title\":\"Slide B\"}");
            var meta3 = JsonSerializer.Deserialize<JsonElement>("{\"content_kind\":\"instruction_text\",\"slide_title\":\"Slide C\"}");
            
            var metadatas = new List<JsonElement> { meta1, meta2, meta3 };

            var contextText = ChatHub.BuildContextText(docs, metadatas, QueryIntent.TaskInstruction);

            Assert.Contains("\u0110O\u1EA0N 1", contextText);
            Assert.Contains("\u0110O\u1EA0N 2", contextText);
            Assert.Contains("\u0110O\u1EA0N 3", contextText);
        }

        [Fact]
        public void PowerPointParser_CleanPptText_ReplacesIconGapsOnly()
        {
            var method = typeof(PowerPointParser).GetMethod("CleanPptText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var tabGap = (string)method.Invoke(null, new object[] { "click nut\t   de tim" })!;
            Assert.Equal("click nut de tim", tabGap);

            var shortSpaces = (string)method.Invoke(null, new object[] { "Chuc   nang" })!;
            Assert.Equal("Chuc nang", shortSpaces);

            var longSpaces = (string)method.Invoke(null, new object[] { "Nhan                de duyet" })!;
            Assert.Equal("Nhan n\u00FAt de duyet", longSpaces);
        }

        [Fact]
        public void PowerPointParser_IsStepLine_IdentifiesStepsCorrectly()
        {
            var method = typeof(PowerPointParser).GetMethod("IsStepLine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            Assert.True((bool)method.Invoke(null, new object[] { "B\u01B0\u1EDBc 1: \u0110\u0103ng nh\u1EADp v\u00E0o h\u1EC7 th\u1ED1ng." })!);
            Assert.True((bool)method.Invoke(null, new object[] { "  Buoc 2 Ch\u1ECDn m\u1EE5c " })!);
            Assert.True((bool)method.Invoke(null, new object[] { "Step 10: Click" })!);
            Assert.True((bool)method.Invoke(null, new object[] { "B3: B\u1EA5m \u0111\u1EC3 ch\u1ECDn" })!);
            
            Assert.False((bool)method.Invoke(null, new object[] { "3. PH\u00C2N H\u1EC6 T\u1EDC TR\u00CCNH NGHI\u1EC6P V\u1EE5" })!);
            Assert.False((bool)method.Invoke(null, new object[] { "T\u1EDD tr\u00ECnh nghi\u1EC7p v\u1EE5 \u2013 Th\u00EAm m\u1EDBi" })!);
        }

        [Fact]
        public void ImageUtilities_ShouldSkipOcr_ForTinyDecorativeImages()
        {
            Assert.True(ImageUtilities.ShouldSkipOcr(2, 36));
            Assert.True(ImageUtilities.ShouldSkipOcr(10, 100));
            Assert.False(ImageUtilities.ShouldSkipOcr(80, 40));
        }

        [Fact]
        public void VisionCaptionQuality_RejectsCjkNoise()
        {
            var type = Type.GetType("RagApi.Services.Ingestion.VisionCaptionQuality, RagApi");
            Assert.NotNull(type);
            var method = type!.GetMethod("IsUseful", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var caption = "IMAGE_TYPE: screenshot\nVISIBLE_TEXT: Tờ trình nghiệp vụ 范本导向\nBUTTONS_OR_ACTIONS: duyệt phiếu\nSEARCHABLE_SUMMARY: giao diện tờ trình nghiệp vụ";
            var result = (bool)method!.Invoke(null, new object?[] { caption, null })!;
            Assert.False(result);
        }

        [Fact]
        public void ChatHub_DetermineMaxTokens_UsesStepAwareBudget()
        {
            var method = typeof(ChatHub).GetMethod("DetermineMaxTokens", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var normal = (int)method!.Invoke(null, new object[] { "Thong tin chung ve to trinh" })!;
            Assert.Equal(600, normal);

            var steps = "Bước 1: Dang nhap\nBước 2: Chon muc\nBước 3: Nhan nut\nBước 4: Luu";
            var stepBudget = (int)method.Invoke(null, new object[] { steps })!;
            Assert.True(stepBudget >= 900);

            var bSteps = "B1: Dang nhap\nB2: Chon muc\nB3: Nhan nut\nB4: Luu";
            var bStepBudget = (int)method.Invoke(null, new object[] { bSteps })!;
            Assert.True(bStepBudget >= 900);
        }
        [Fact]
        public void NearDuplicateDetector_LogsWarning_WhenMetadataDiffers()
        {
            var testLogger = new TestLogger();
            
            var docText1 = "Dòng hướng dẫn thao tác 1\nDòng hướng dẫn thao tác 2\nDòng hướng dẫn thao tác 3\nDòng hướng dẫn thao tác 4\nDòng hướng dẫn thao tác 5";
            var docText2 = "Dòng hướng dẫn thao tác 1\nDòng hướng dẫn thao tác 2\nDòng hướng dẫn thao tác 3\nDòng hướng dẫn thao tác 4\nDòng hướng dẫn thao tác 6"; // 4/5 = 80% overlap
            
            var doc1 = new IngestedDocument
            {
                PageContent = docText1,
                Metadata = new Dictionary<string, object>
                {
                    { "content_kind", "instruction_text" },
                    { "slide_title", "Slide A" },
                    { "action_kind", "duyet" }
                }
            };
            var doc2 = new IngestedDocument
            {
                PageContent = docText2,
                Metadata = new Dictionary<string, object>
                {
                    { "content_kind", "instruction_text" },
                    { "slide_title", "Slide B" },
                    { "action_kind", "tu_choi" }
                }
            };
            
            NearDuplicateDetector.DetectAndLog(new List<IngestedDocument> { doc1, doc2 }, "test.pptx", testLogger);
            
            Assert.True(testLogger.LoggedWarning);
            Assert.Contains("Slide A", testLogger.LastLogMessage);
            Assert.Contains("duyet", testLogger.LastLogMessage);
            Assert.Contains("Slide B", testLogger.LastLogMessage);
            Assert.Contains("tu_choi", testLogger.LastLogMessage);
        }

        private class TestLogger : Microsoft.Extensions.Logging.ILogger
        {
            public bool LoggedWarning { get; private set; }
            public string LastLogMessage { get; private set; } = "";

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                {
                    LoggedWarning = true;
                    LastLogMessage = formatter(state, exception);
                }
            }
        }

        [Fact]
        public void PowerPointParser_Reflection_StepMethods_WorkAsExpected()
        {
            var countMethod = typeof(PowerPointParser).GetMethod("CountSteps", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(countMethod);

            var splitMethod = typeof(PowerPointParser).GetMethod("SplitProcedureBlocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(splitMethod);

            // Test CountSteps
            var count1 = (int)countMethod!.Invoke(null, new object[] { "Bước 1: Dang nhap\nB2: Chon muc\nBuoc 3: Nhan nut" })!;
            Assert.Equal(3, count1);

            // Test SplitProcedureBlocks with B1:
            var textToSplit = "B1: Buoc dau tien\nB2: Buoc thu hai\nB1: Buoc dau tien cua quy trinh 2\nB2: Buoc tiep theo";
            var blocks = (List<string>)splitMethod!.Invoke(null, new object[] { textToSplit })!;
            Assert.Equal(2, blocks.Count);
            Assert.Contains("B1: Buoc dau tien", blocks[0]);
            Assert.Contains("B1: Buoc dau tien cua quy trinh 2", blocks[1]);
        }

    }
}
