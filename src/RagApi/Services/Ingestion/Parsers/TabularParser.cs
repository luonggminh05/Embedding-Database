using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

namespace RagApi.Services.Ingestion.Parsers;

public class TabularParser : IDocumentParser
{
    public bool CanParse(string extension) => extension == ".xlsx" || extension == ".csv";

    public Task<List<IngestedDocument>> ParseAsync(string filePath, string fileName)
    {
        var docs = new List<IngestedDocument>();
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (extension == ".xlsx")
        {
            using var workbook = new XLWorkbook(filePath);
            var worksheet = workbook.Worksheets.FirstOrDefault();
            if (worksheet != null)
            {
                var firstRow = worksheet.FirstRowUsed();
                if (firstRow != null)
                {
                    var headers = firstRow.CellsUsed().ToDictionary(c => c.Address.ColumnNumber, c => c.GetString());
                    
                    foreach (var row in worksheet.RowsUsed().Skip(1))
                    {
                        var rowData = new List<string>();
                        foreach (var cell in row.CellsUsed())
                        {
                            if (headers.TryGetValue(cell.Address.ColumnNumber, out var headerName))
                            {
                                rowData.Add($"{headerName}: {cell.GetString()}");
                            }
                        }
                        if (rowData.Count > 0)
                        {
                            docs.Add(new IngestedDocument
                            {
                                PageContent = string.Join(", ", rowData),
                                Metadata = new Dictionary<string, object>
                                {
                                    { "source", fileName },
                                    { "row", row.RowNumber() }
                                }
                            });
                        }
                    }
                }
            }
        }
        else if (extension == ".csv")
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true };
            using var reader = new StreamReader(filePath);
            using var csv = new CsvReader(reader, config);
            csv.Read();
            csv.ReadHeader();
            var headers = csv.HeaderRecord;

            if (headers != null)
            {
                int rowIndex = 2; // 1 is header
                while (csv.Read())
                {
                    var rowData = new List<string>();
                    for (int i = 0; i < headers.Length; i++)
                    {
                        var field = csv.GetField(i);
                        if (!string.IsNullOrWhiteSpace(field))
                        {
                            rowData.Add($"{headers[i]}: {field}");
                        }
                    }
                    if (rowData.Count > 0)
                    {
                        docs.Add(new IngestedDocument
                        {
                            PageContent = string.Join(", ", rowData),
                            Metadata = new Dictionary<string, object>
                            {
                                { "source", fileName },
                                { "row", rowIndex }
                            }
                        });
                    }
                    rowIndex++;
                }
            }
        }

        return Task.FromResult(docs);
    }
}
