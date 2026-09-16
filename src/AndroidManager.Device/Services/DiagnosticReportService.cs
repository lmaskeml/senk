using System.IO;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class DiagnosticReportService : IDiagnosticReportService
{
    private readonly ILogger _logger;

    static DiagnosticReportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public DiagnosticReportService(ILogger? logger = null)
    {
        _logger = logger ?? Log.ForContext<DiagnosticReportService>();
    }

    public async Task<string> GeneratePdfAsync(
        DeviceInfo info,
        DeviceDiagnostics diagnostics,
        string saveDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(saveDirectory);
        var safeModel = SanitizeFilePart(info.Model);
        var path = Path.Combine(
            saveDirectory,
            $"diagnostic_{safeModel}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

        var battery = diagnostics.Battery;
        var storage = diagnostics.Storage;
        var ram = diagnostics.Ram;

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Header().Element(ComposeHeader);
                    page.Content().Element(c => ComposeContent(c, info, battery, storage, ram, diagnostics));
                    page.Footer().AlignRight().Text(t =>
                    {
                        t.Span("SeND ANDROID MANAGER · ").FontSize(8).FontColor(Colors.Grey.Medium);
                        t.CurrentPageNumber().FontSize(8);
                        t.Span(" / ").FontSize(8);
                        t.TotalPages().FontSize(8);
                    });
                });
            }).GeneratePdf(path);
        }, cancellationToken).ConfigureAwait(false);

        _logger.Information("PDF diagnostics report: {Path}", path);
        return path;
    }

    public async Task<string> GenerateTxtAsync(
        DeviceInfo info,
        DeviceDiagnostics diagnostics,
        string saveDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(saveDirectory);
        var safeModel = SanitizeFilePart(info.Model);
        var path = Path.Combine(
            saveDirectory,
            $"diagnostic_{safeModel}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        var body = diagnostics.SummaryText;
        if (!string.IsNullOrWhiteSpace(diagnostics.RawBatteryDump))
            body += "\n--- dumpsys battery ---\n" + diagnostics.RawBatteryDump.Trim() + "\n";

        body = $"Üretici: {info.Manufacturer}\nModel: {info.Model}\nAndroid: {info.AndroidVersion}\n" +
               $"API: {info.ApiLevel}\nSeri: {info.Serial}\n\n" + body;

        await File.WriteAllTextAsync(path, body, cancellationToken).ConfigureAwait(false);
        _logger.Information("TXT diagnostics report: {Path}", path);
        return path;
    }

    private static void ComposeHeader(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().Text("SeND ANDROID MANAGER").SemiBold().FontSize(20).FontColor(Colors.Blue.Medium);
            col.Item().Text("Cihaz Tanı Raporu").FontSize(12).FontColor(Colors.Grey.Medium);
            col.Item().Text($"Oluşturulma: {DateTime.Now:dd MMMM yyyy HH:mm}")
                .FontSize(9).FontColor(Colors.Grey.Medium);
            col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Blue.Medium);
        });
    }

    private static void ComposeContent(
        IContainer container,
        DeviceInfo info,
        BatteryInfo battery,
        StorageInfo storage,
        RamInfo ram,
        DeviceDiagnostics diagnostics)
    {
        container.PaddingTop(12).Column(col =>
        {
            col.Spacing(14);

            col.Item().Element(c => Section(c, "Cihaz Bilgisi",
            [
                ("Üretici", info.Manufacturer),
                ("Model", info.Model),
                ("Android", info.AndroidVersion),
                ("API", info.ApiLevel),
                ("Seri No", info.Serial)
            ]));

            col.Item().Element(c => Section(c, "Pil",
            [
                ("Doluluk", $"%{battery.Level}"),
                ("Sıcaklık", $"{battery.Temperature:F1} °C"),
                ("Voltaj", $"{battery.Voltage} mV"),
                ("Şarj", battery.IsCharging ? "Evet" : "Hayır"),
                ("Sağlık", string.IsNullOrWhiteSpace(battery.Health) ? "—" : battery.Health),
                ("Durum", string.IsNullOrWhiteSpace(diagnostics.Status) ? battery.Status : diagnostics.Status)
            ]));

            col.Item().Element(c => Section(c, "Depolama",
            [
                ("Toplam", storage.TotalFormatted),
                ("Kullanılan", storage.UsedFormatted),
                ("Boş", storage.FreeFormatted),
                ("Kullanım", $"%{storage.UsagePercent:F1}")
            ]));

            col.Item().Element(c => Section(c, "RAM",
            [
                ("Toplam", ram.TotalFormatted),
                ("Kullanılan", ram.UsedFormatted),
                ("Kullanılabilir", ram.AvailableFormatted),
                ("Kullanım", $"%{ram.UsagePercent:F1}")
            ]));

            col.Item().Background(Colors.Blue.Lighten5).Padding(10).Column(sum =>
            {
                sum.Item().Text("Özet").SemiBold().FontColor(Colors.Blue.Medium);
                sum.Item().Text(BatteryEval(battery.Level));
                sum.Item().Text(StorageEval(storage.UsagePercent));
                sum.Item().Text(RamEval(ram.UsagePercent));
            });
        });
    }

    private static void Section(IContainer container, string title, (string Label, string Value)[] rows)
    {
        container.Column(col =>
        {
            col.Item().Text(title).SemiBold().FontSize(12).FontColor(Colors.Blue.Medium);
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(2);
                    c.RelativeColumn(3);
                });
                foreach (var (label, value) in rows)
                {
                    table.Cell().PaddingVertical(2).Text(label).FontColor(Colors.Grey.Darken1);
                    table.Cell().PaddingVertical(2).Text(value).SemiBold();
                }
            });
        });
    }

    private static string BatteryEval(int level) => level switch
    {
        >= 80 => "Pil: sağlıklı",
        >= 50 => "Pil: orta seviye",
        _ => "Pil: şarj edilmeli"
    };

    private static string StorageEval(double pct) => pct switch
    {
        <= 70 => "Depolama: yeterli",
        <= 85 => "Depolama: dolmak üzere",
        _ => "Depolama: kritik"
    };

    private static string RamEval(double pct) => pct switch
    {
        <= 70 => "RAM: normal",
        <= 85 => "RAM: yüksek",
        _ => "RAM: kritik"
    };

    private static string SanitizeFilePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "device";
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value.Length > 40 ? value[..40] : value;
    }
}
