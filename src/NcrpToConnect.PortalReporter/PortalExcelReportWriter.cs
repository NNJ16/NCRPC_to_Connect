using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Xml;
using NcrpToConnect.Cli;

namespace NcrpToConnect.PortalReporter;

internal static class PortalExcelReportWriter
{
    public static void Write(PortalExportReport report, string outputPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        WriteContentTypes(archive);
        WriteRootRelationships(archive);
        WriteWorkbook(archive);
        WriteWorkbookRelationships(archive);
        WriteStyles(archive);
        WriteSummarySheet(archive, report);
        WriteDetailsSheet(archive, report);
    }

    private static void WriteSummarySheet(ZipArchive archive, PortalExportReport report)
    {
        var rows = new List<IReadOnlyList<CellValue>>
        {
            new[] { Text("Portal PTL Report", header: true) },
            new[] { Text("Source"), Text(report.SourcePath) },
            new[] { Text("Pages"), Number(report.PageCount) },
            new[] { Text("Connectors"), Number(report.ConnectorCount) },
            new[] { Text("Workflows"), Number(report.WorkflowCount) },
            new[] { Text("LOVs"), Number(report.LovCount) },
            Array.Empty<CellValue>(),
            new[] { Text("Page title", header: true), Text("Portlet count", header: true) }
        };

        foreach (var page in report.Pages)
        {
            rows.Add(new[] { Text(page.Title), Number(page.PortletCount) });
        }

        WriteWorksheet(
            archive,
            "xl/worksheets/sheet1.xml",
            rows,
            columnWidths: [36, 18],
            frozenHeaderRows: 8);
    }

    private static void WriteDetailsSheet(ZipArchive archive, PortalExportReport report)
    {
        var rows = new List<IReadOnlyList<CellValue>>
        {
            new[]
            {
                Text("Page title", header: true),
                Text("Portlet count", header: true),
                Text("Portlet title", header: true),
                Text("Portlet type", header: true),
                Text("Connectors", header: true),
                Text("Workflows", header: true),
                Text("LOVs", header: true)
            }
        };

        foreach (var page in report.Pages)
        {
            if (page.Portlets.Count == 0)
            {
                rows.Add(new[] { Text(page.Title), Number(page.PortletCount), Text("-"), Text("-"), Text("-"), Text("-"), Text("-") });
                continue;
            }

            foreach (var portlet in page.Portlets)
            {
                rows.Add(
                new[]
                {
                    Text(page.Title),
                    Number(page.PortletCount),
                    Text(portlet.Title),
                    Text(portlet.PortletType),
                    Text(Join(portlet.Connectors)),
                    Text(JoinReferences(portlet.Workflows)),
                    Text(JoinReferences(portlet.Lovs))
                });
            }
        }

        WriteWorksheet(
            archive,
            "xl/worksheets/sheet2.xml",
            rows,
            columnWidths: [32, 15, 30, 16, 30, 70, 58],
            frozenHeaderRows: 1);
    }

    private static void WriteWorksheet(
        ZipArchive archive,
        string entryName,
        IReadOnlyList<IReadOnlyList<CellValue>> rows,
        IReadOnlyList<double> columnWidths,
        int frozenHeaderRows)
    {
        using var writer = CreateXmlWriter(archive, entryName);
        writer.WriteStartDocument(true);
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, RelationshipsNamespace);

        writer.WriteStartElement("sheetViews");
        writer.WriteStartElement("sheetView");
        writer.WriteAttributeString("workbookViewId", "0");
        if (frozenHeaderRows > 0)
        {
            writer.WriteStartElement("pane");
            writer.WriteAttributeString("ySplit", frozenHeaderRows.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("topLeftCell", $"A{frozenHeaderRows + 1}");
            writer.WriteAttributeString("activePane", "bottomLeft");
            writer.WriteAttributeString("state", "frozen");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("cols");
        for (var i = 0; i < columnWidths.Count; i++)
        {
            writer.WriteStartElement("col");
            writer.WriteAttributeString("min", (i + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("max", (i + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("width", columnWidths[i].ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        writer.WriteStartElement("sheetData");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var rowNumber = rowIndex + 1;
            var row = rows[rowIndex];
            writer.WriteStartElement("row");
            writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
            if (row.Any(cell => cell.TextValue?.Length > 70))
            {
                writer.WriteAttributeString("ht", "45");
                writer.WriteAttributeString("customHeight", "1");
            }

            for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
            {
                WriteCell(writer, row[columnIndex], rowNumber, columnIndex + 1);
            }

            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        writer.WriteStartElement("autoFilter");
        writer.WriteAttributeString("ref", $"A1:{ColumnName(columnWidths.Count)}{Math.Max(rows.Count, 1)}");
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteCell(XmlWriter writer, CellValue cell, int rowNumber, int columnNumber)
    {
        writer.WriteStartElement("c");
        writer.WriteAttributeString("r", $"{ColumnName(columnNumber)}{rowNumber}");
        writer.WriteAttributeString("s", cell.Header ? "1" : "0");

        if (cell.NumberValue.HasValue)
        {
            writer.WriteStartElement("v");
            writer.WriteString(cell.NumberValue.Value.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }
        else
        {
            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is");
            writer.WriteStartElement("t");
            writer.WriteString(cell.TextValue ?? string.Empty);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteContentTypes(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "[Content_Types].xml");
        writer.WriteStartDocument(true);
        writer.WriteStartElement("Types", ContentTypesNamespace);
        WriteDefault(writer, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        WriteDefault(writer, "xml", "application/xml");
        WriteOverride(writer, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        WriteOverride(writer, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        WriteOverride(writer, "/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        WriteOverride(writer, "/xl/worksheets/sheet2.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteRootRelationships(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "_rels/.rels");
        writer.WriteStartDocument(true);
        writer.WriteStartElement("Relationships", PackageRelationshipsNamespace);
        WriteRelationship(writer, "rId1", OfficeDocumentRelationship, "xl/workbook.xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbook(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "xl/workbook.xml");
        writer.WriteStartDocument(true);
        writer.WriteStartElement("workbook", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, RelationshipsNamespace);
        writer.WriteStartElement("sheets");
        WriteSheet(writer, "Summary", 1, "rId1");
        WriteSheet(writer, "Portlet Details", 2, "rId2");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbookRelationships(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "xl/_rels/workbook.xml.rels");
        writer.WriteStartDocument(true);
        writer.WriteStartElement("Relationships", PackageRelationshipsNamespace);
        WriteRelationship(writer, "rId1", WorksheetRelationship, "worksheets/sheet1.xml");
        WriteRelationship(writer, "rId2", WorksheetRelationship, "worksheets/sheet2.xml");
        WriteRelationship(writer, "rId3", StylesRelationship, "styles.xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteStyles(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "xl/styles.xml");
        writer.WriteStartDocument(true);
        writer.WriteStartElement("styleSheet", SpreadsheetNamespace);

        writer.WriteStartElement("fonts");
        writer.WriteAttributeString("count", "2");
        WriteFont(writer, bold: false);
        WriteFont(writer, bold: true);
        writer.WriteEndElement();

        writer.WriteStartElement("fills");
        writer.WriteAttributeString("count", "2");
        WritePatternFill(writer, "none");
        WritePatternFill(writer, "gray125");
        writer.WriteEndElement();

        writer.WriteStartElement("borders");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("border");
        writer.WriteElementString("left", string.Empty);
        writer.WriteElementString("right", string.Empty);
        writer.WriteElementString("top", string.Empty);
        writer.WriteElementString("bottom", string.Empty);
        writer.WriteElementString("diagonal", string.Empty);
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyleXfs");
        writer.WriteAttributeString("count", "1");
        WriteXf(writer, fontId: 0, applyFont: false);
        writer.WriteEndElement();

        writer.WriteStartElement("cellXfs");
        writer.WriteAttributeString("count", "2");
        WriteXf(writer, fontId: 0, applyFont: false);
        WriteXf(writer, fontId: 1, applyFont: true);
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteFont(XmlWriter writer, bool bold)
    {
        writer.WriteStartElement("font");
        if (bold)
        {
            writer.WriteElementString("b", string.Empty);
        }
        writer.WriteStartElement("sz");
        writer.WriteAttributeString("val", "11");
        writer.WriteEndElement();
        writer.WriteStartElement("name");
        writer.WriteAttributeString("val", "Calibri");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WritePatternFill(XmlWriter writer, string patternType)
    {
        writer.WriteStartElement("fill");
        writer.WriteStartElement("patternFill");
        writer.WriteAttributeString("patternType", patternType);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteXf(XmlWriter writer, int fontId, bool applyFont)
    {
        writer.WriteStartElement("xf");
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString("fontId", fontId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fillId", "0");
        writer.WriteAttributeString("borderId", "0");
        writer.WriteAttributeString("xfId", "0");
        if (applyFont)
        {
            writer.WriteAttributeString("applyFont", "1");
        }
        writer.WriteStartElement("alignment");
        writer.WriteAttributeString("vertical", "top");
        writer.WriteAttributeString("wrapText", "1");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteDefault(XmlWriter writer, string extension, string contentType)
    {
        writer.WriteStartElement("Default");
        writer.WriteAttributeString("Extension", extension);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WriteOverride(XmlWriter writer, string partName, string contentType)
    {
        writer.WriteStartElement("Override");
        writer.WriteAttributeString("PartName", partName);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WriteRelationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationship");
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }

    private static void WriteSheet(XmlWriter writer, string name, int sheetId, string relationshipId)
    {
        writer.WriteStartElement("sheet");
        writer.WriteAttributeString("name", name);
        writer.WriteAttributeString("sheetId", sheetId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("r", "id", null, relationshipId);
        writer.WriteEndElement();
    }

    private static XmlWriter CreateXmlWriter(ZipArchive archive, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        return XmlWriter.Create(entry.Open(), new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            CloseOutput = true
        });
    }

    private static CellValue Text(string? value, bool header = false) => new(value ?? string.Empty, null, header);

    private static CellValue Number(int value) => new(null, value, Header: false);

    private static string Join(IEnumerable<string> values)
    {
        var materialized = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
        return materialized.Length == 0 ? "-" : string.Join(", ", materialized);
    }

    private static string JoinReferences(IEnumerable<PortalReference> references)
    {
        var materialized = references.Select(r => r.Name).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
        return materialized.Length == 0 ? "-" : string.Join(", ", materialized);
    }

    private static string ColumnName(int columnNumber)
    {
        var dividend = columnNumber;
        var columnName = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }

        return columnName;
    }

    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string OfficeDocumentRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string WorksheetRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";
    private const string StylesRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";

    private sealed record CellValue(string? TextValue, int? NumberValue, bool Header);
}
