using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Serilog;
using WeChatExport.Core.Models;

namespace WeChatExport.Services;

public class ExportService
{
    private readonly ILogger _logger;

    public ExportService()
    {
        _logger = Log.ForContext<ExportService>();
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public void ExportToJson(Conversation conversation, string outputPath)
    {
        try
        {
            var exportData = new
            {
                Contact = conversation.Contact,
                Messages = conversation.Messages.Select(m => new
                {
                    m.MessageId,
                    m.SenderName,
                    m.Content,
                    Type = m.Type.ToString(),
                    m.CreateTime,
                    m.IsFromSelf
                }),
                conversation.TotalMessageCount
            };

            var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = true,
                // Preserve CJK and other non-ASCII text literally instead of \uXXXX
                // escapes -- this is a Chinese chat export, users read it directly.
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
            });

            File.WriteAllText(outputPath, json);
            _logger.Information("Exported to JSON: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export to JSON");
            throw;
        }
    }

    public void ExportToCsv(Conversation conversation, string outputPath)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Time,Sender,Content,Type,FromSelf");

            foreach (var msg in conversation.Messages)
            {
                var content = msg.Content?.Replace("\"", "\"\"") ?? "";
                sb.AppendLine($"\"{msg.CreateTime:yyyy-MM-dd HH:mm:ss}\",\"{msg.SenderName}\",\"{content}\",\"{msg.Type}\",\"{msg.IsFromSelf}\"");
            }

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            _logger.Information("Exported to CSV: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export to CSV");
            throw;
        }
    }

    public void ExportToTxt(Conversation conversation, string outputPath)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Chat with: {conversation.Contact.DisplayName}");
            sb.AppendLine(new string('=', 50));
            sb.AppendLine();

            foreach (var msg in conversation.Messages.OrderBy(m => m.CreateTime))
            {
                var sender = msg.IsFromSelf ? "You" : (msg.SenderName ?? "Unknown");
                sb.AppendLine($"[{msg.CreateTime:yyyy-MM-dd HH:mm:ss}] {sender}:");
                sb.AppendLine($"  {msg.Content}");
                sb.AppendLine();
            }

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            _logger.Information("Exported to TXT: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export to TXT");
            throw;
        }
    }

    public void ExportToHtml(Conversation conversation, string outputPath)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset='utf-8'>");
            sb.AppendLine("<title>Chat Export</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; }");
            sb.AppendLine(".message { margin: 10px 0; padding: 10px; border-radius: 8px; }");
            sb.AppendLine(".self { background: #E0F0FF; margin-left: 50px; }");
            sb.AppendLine(".other { background: #F0F0F0; margin-right: 50px; }");
            sb.AppendLine(".time { font-size: 11px; color: #888; }");
            sb.AppendLine(".sender { font-weight: bold; font-size: 12px; }");
            sb.AppendLine("</style></head><body>");

            sb.AppendLine($"<h1>Chat with: {conversation.Contact.DisplayName}</h1>");

            foreach (var msg in conversation.Messages.OrderBy(m => m.CreateTime))
            {
                var cssClass = msg.IsFromSelf ? "self" : "other";
                var sender = msg.IsFromSelf ? "You" : (msg.SenderName ?? "Unknown");

                sb.AppendLine($"<div class='message {cssClass}'>");
                sb.AppendLine($"<div class='sender'>{sender}</div>");
                sb.AppendLine($"<div>{msg.Content?.Replace("\n", "<br>")}</div>");
                sb.AppendLine($"<div class='time'>{msg.CreateTime:yyyy-MM-dd HH:mm:ss}</div>");
                sb.AppendLine("</div>");
            }

            sb.AppendLine("</body></html>");

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            _logger.Information("Exported to HTML: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export to HTML");
            throw;
        }
    }

    public void ExportToExcel(Conversation conversation, string outputPath)
    {
        try
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Chat");

            worksheet.Cell(1, 1).Value = "Time";
            worksheet.Cell(1, 2).Value = "Sender";
            worksheet.Cell(1, 3).Value = "Content";
            worksheet.Cell(1, 4).Value = "Type";
            worksheet.Cell(1, 5).Value = "From Self";

            var headerRange = worksheet.Range(1, 1, 1, 5);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

            var row = 2;
            foreach (var msg in conversation.Messages.OrderBy(m => m.CreateTime))
            {
                worksheet.Cell(row, 1).Value = msg.CreateTime.ToString("yyyy-MM-dd HH:mm:ss");
                worksheet.Cell(row, 2).Value = msg.SenderName ?? "Unknown";
                worksheet.Cell(row, 3).Value = msg.Content ?? "";
                worksheet.Cell(row, 4).Value = msg.Type.ToString();
                worksheet.Cell(row, 5).Value = msg.IsFromSelf ? "Yes" : "No";
                row++;
            }

            worksheet.Columns().AdjustToContents();
            workbook.SaveAs(outputPath);
            _logger.Information("Exported to Excel: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export to Excel");
            throw;
        }
    }

    public void ExportToPdf(Conversation conversation, string outputPath)
    {
        try
        {
            var messages = conversation.Messages.OrderBy(m => m.CreateTime).ToList();

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(20);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Header()
                        .Text($"Chat with: {conversation.Contact.DisplayName}")
                        .FontSize(16).Bold().FontColor(Colors.Blue.Darken2);

                    page.Content().Column(column =>
                    {
                        foreach (var msg in messages)
                        {
                            var sender = msg.IsFromSelf ? "You" : (msg.SenderName ?? "Unknown");
                            var bgColor = msg.IsFromSelf ? Colors.Grey.Lighten4 : Colors.White;

                            column.Item().Background(bgColor).Padding(10).Column(msgCol =>
                            {
                                msgCol.Item().Row(row =>
                                {
                                    row.RelativeItem().Text(sender).Bold();
                                    row.AutoItem().Text(msg.CreateTime.ToString("yyyy-MM-dd HH:mm"))
                                        .FontSize(9).FontColor(Colors.Grey.Medium);
                                });

                                msgCol.Item().PaddingTop(5).Text(msg.Content ?? "").LineHeight(1.5f);
                            });
                        }
                    });

                    page.Footer()
                        .AlignCenter()
                        .Text(text => text.CurrentPageNumber().FontSize(9));
                });
            }).GeneratePdf(outputPath);

            _logger.Information("Exported to PDF: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export to PDF");
            throw;
        }
    }
}
