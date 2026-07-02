using System;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using RagApi.Services.Ingestion;
using System.Collections.Generic;

using Shape = DocumentFormat.OpenXml.Presentation.Shape;
using SlideId = DocumentFormat.OpenXml.Presentation.SlideId;
using GraphicFrame = DocumentFormat.OpenXml.Presentation.GraphicFrame;
using Picture = DocumentFormat.OpenXml.Presentation.Picture;
using Table = DocumentFormat.OpenXml.Drawing.Table;

class Program
{
    static string GetShapeText(Shape shape)
    {
        var paragraphs = shape.Descendants<DocumentFormat.OpenXml.Drawing.Paragraph>();
        var paragraphTexts = new List<string>();
        foreach (var p in paragraphs)
        {
            var pText = string.Concat(p.Descendants<DocumentFormat.OpenXml.Drawing.Text>().Select(t => t.Text));
            if (!string.IsNullOrWhiteSpace(pText))
            {
                paragraphTexts.Add(pText);
            }
        }
        return string.Join("\n", paragraphTexts);
    }

    static void Main(string[] args)
    {
        var filePath = @"f:\BKU\Intern\Host\papers\Slide dao tao Phan mem QLTS GD 3 - Tờ trình nghiệp vụ.pptx";
        if (!File.Exists(filePath))
        {
            Console.WriteLine("File not found: " + filePath);
            return;
        }

        using var presentationDocument = PresentationDocument.Open(filePath, false);
        var presentationPart = presentationDocument.PresentationPart;
        if (presentationPart?.Presentation?.SlideIdList == null)
        {
            Console.WriteLine("No slides found.");
            return;
        }

        int targetSlideIndex = 6; // Slide 6
        int slideIndex = 1;
        foreach (var slideId in presentationPart.Presentation.SlideIdList.Elements<SlideId>())
        {
            if (slideIndex == targetSlideIndex)
            {
                var slidePart = (SlidePart)presentationPart.GetPartById(slideId.RelationshipId!);
                var slide = slidePart.Slide;
                var shapeTree = slide?.CommonSlideData?.ShapeTree;
                
                Console.WriteLine($"\n==========================================");
                Console.WriteLine($"=== TARGET SLIDE {slideIndex} ===");
                Console.WriteLine($"==========================================");
                
                var instructionalTexts = new List<string>();
                int elemIdx = 1;
                foreach (var element in shapeTree.Elements())
                {
                    string elemType = element.GetType().Name;
                    string elemText = "";
                    if (element is Shape shape)
                    {
                        elemText = GetShapeText(shape);
                        if (!string.IsNullOrWhiteSpace(elemText))
                        {
                            instructionalTexts.Add(elemText);
                        }
                    }
                    else if (element is GraphicFrame graphicFrame)
                    {
                        var table = graphicFrame.Descendants<Table>().FirstOrDefault();
                        if (table != null)
                        {
                            var rows = new List<string>();
                            foreach (var row in table.Descendants<DocumentFormat.OpenXml.Drawing.TableRow>())
                            {
                                var rowTexts = row.Descendants<DocumentFormat.OpenXml.Drawing.TableCell>()
                                    .Select(cell => string.Join(" ", cell.Descendants<DocumentFormat.OpenXml.Drawing.Text>().Select(t => t.Text)).Trim())
                                    .Where(t => !string.IsNullOrWhiteSpace(t));
                                var rowString = string.Join(" | ", rowTexts);
                                if (!string.IsNullOrWhiteSpace(rowString)) rows.Add(rowString);
                            }
                            elemText = "[TABLE] " + string.Join("\n", rows);
                            instructionalTexts.AddRange(rows);
                        }
                        else
                        {
                            elemText = string.Join(" ", graphicFrame.Descendants<DocumentFormat.OpenXml.Drawing.Text>().Select(t => t.Text)).Trim();
                            if (!string.IsNullOrWhiteSpace(elemText))
                            {
                                instructionalTexts.Add(elemText);
                            }
                        }
                    }
                    else if (element is Picture picture)
                    {
                        elemText = "[PICTURE]";
                        instructionalTexts.Add("[Ảnh giao diện]");
                    }
                    
                    if (!string.IsNullOrWhiteSpace(elemText))
                    {
                        Console.WriteLine($"Element {elemIdx} ({elemType}): {elemText}");
                    }
                    elemIdx++;
                }

                var fullText = string.Join("\n", instructionalTexts);
                Console.WriteLine($"\n--- JOINED TEXT (Length: {fullText.Length}) ---");
                Console.WriteLine(fullText);
            }
            slideIndex++;
        }
    }
}
