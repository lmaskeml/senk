using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Security.Services;

public sealed class VtException : Exception
{
    public bool IsQuota { get; init; }

    public VtException(string message, bool isQuota = false, Exception? inner = null)
        : base(message, inner)
    {
        IsQuota = isQuota;
    }
}

public sealed class VirusTotalService : IVirusTotalService, IDisposable
{
    private const string BaseUrl = "https://www.virustotal.com/api/v3/";
    private const long MaxDirectUploadBytes = 32L * 1024 * 1024;

    private static readonly string SecurityDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidManager", "security");

    private static readonly string ConfigPath = Path.Combine(SecurityDir, "vt_config.json");
    private static readonly string CachePath = Path.Combine(SecurityDir, "vt_cache.json");
    private static readonly string UsagePath = Path.Combine(SecurityDir, "vt_usage.json");

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VtConfig _config;
    private Dictionary<string, VtReport> _cache = new(StringComparer.OrdinalIgnoreCase);
    private VtUsage _usage = new();
    private DateTime _nextAllowedAt = DateTime.MinValue;

    public VtConfig Config => _config;

    public VirusTotalService(ILogger? logger = null)
    {
        _logger = logger ?? Log.ForContext<VirusTotalService>();
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
        _config = LoadConfig();
        _cache = LoadCache();
        _usage = LoadUsage();
        ResetUsageIfNewDay();
        ApplyApiKeyHeader();
    }

    public async Task<bool> SaveConfigAsync(VtConfig config, CancellationToken cancellationToken = default)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        Directory.CreateDirectory(SecurityDir);
        var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(ConfigPath, json, cancellationToken).ConfigureAwait(false);
        ApplyApiKeyHeader();
        _logger.Information("VirusTotal yapılandırması kaydedildi (sim={Sim})", _config.UseSimulation);
        return true;
    }

    public async Task<VtReport> CheckHashAsync(string hash, CancellationToken cancellationToken = default)
    {
        hash = NormalizeHash(hash);
        if (!IsValidHashFormat(hash))
            throw new VtException("Geçersiz hash (MD5:32 / SHA1:40 / SHA256:64).");

        if (_cache.TryGetValue(hash, out var cached))
            return cached;

        if (_config.UseSimulation || string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            var sim = SimulateReport(hash);
            Cache(hash, sim);
            return sim;
        }

        await ThrottleAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var resp = await SendWithRetryAsync(
                    HttpMethod.Get,
                    $"files/{hash}",
                    content: null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                var unknown = new VtReport
                {
                    RequestedHash = hash,
                    Verdict = VtVerdict.Unknown
                };
                Cache(hash, unknown);
                RecordRequest();
                return unknown;
            }

            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new VtException(ParseError(body) ?? $"VT HTTP {(int)resp.StatusCode}");

            var report = ParseFileReport(hash, body);
            Cache(hash, report);
            RecordRequest();
            return report;
        }
        catch (VtException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "VT hash sorgusu başarısız: {Hash}", hash);
            return new VtReport { RequestedHash = hash, Verdict = VtVerdict.Error };
        }
    }

    public async Task<List<VtReport>> CheckBatchAsync(
        IEnumerable<string> hashes,
        IProgress<VtProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var list = hashes
            .Select(NormalizeHash)
            .Where(IsValidHashFormat)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<VtReport>(list.Count);
        var sw = Stopwatch.StartNew();
        var malicious = 0;

        for (var i = 0; i < list.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = list[i];
            try
            {
                var report = await CheckHashAsync(hash, cancellationToken).ConfigureAwait(false);
                results.Add(report);
                if (report.Verdict == VtVerdict.Malicious)
                    malicious++;
            }
            catch (VtException ex) when (ex.IsQuota)
            {
                break;
            }

            progress?.Report(new VtProgress
            {
                CurrentItem = hash,
                Done = i + 1,
                Total = list.Count,
                MaliciousFound = malicious,
                Elapsed = sw.Elapsed.ToString(@"mm\:ss")
            });
        }

        return results;
    }

    public async Task<VtReport?> UploadAndScanAsync(
        string localFilePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localFilePath))
            throw new FileNotFoundException("Dosya bulunamadı.", localFilePath);

        if (_config.UseSimulation || string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            progress?.Report("Simülasyon: dosya hash hesaplanıyor…");
            var hash = await ComputeSha256Async(localFilePath, cancellationToken).ConfigureAwait(false);
            var sim = SimulateReport(hash);
            Cache(hash, sim);
            return sim;
        }

        await ThrottleAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report("VirusTotal'e yükleniyor…");

        var fileInfo = new FileInfo(localFilePath);
        string analysisId;

        if (fileInfo.Length <= MaxDirectUploadBytes)
        {
            analysisId = await UploadSmallFileAsync(localFilePath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            analysisId = await UploadLargeFileAsync(localFilePath, cancellationToken).ConfigureAwait(false);
        }

        RecordRequest();
        progress?.Report("Analiz bekleniyor…");

        var sha256 = await PollAnalysisAsync(analysisId, progress, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(sha256))
            return null;

        return await CheckHashAsync(sha256, cancellationToken).ConfigureAwait(false);
    }

    public Task<VtStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetUsageIfNewDay();
        return Task.FromResult(new VtStatus
        {
            HasApiKey = !string.IsNullOrWhiteSpace(_config.ApiKey),
            Simulation = _config.UseSimulation || string.IsNullOrWhiteSpace(_config.ApiKey),
            RequestsToday = _usage.Count,
            DailyQuota = _config.DailyQuota,
            NextAllowedAt = _nextAllowedAt,
            CacheEntries = _cache.Count
        });
    }

    public void Dispose()
    {
        _gate.Dispose();
        _http.Dispose();
    }

    private async Task<string> UploadSmallFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", Path.GetFileName(path));

        using var resp = await SendWithRetryAsync(HttpMethod.Post, "files", content, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new VtException(ParseError(json) ?? $"Yükleme HTTP {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").GetProperty("id").GetString()
               ?? throw new VtException("Analiz kimliği alınamadı.");
    }

    private async Task<string> UploadLargeFileAsync(string path, CancellationToken ct)
    {
        using var urlResp = await SendWithRetryAsync(HttpMethod.Get, "files/upload_url", null, ct)
            .ConfigureAwait(false);
        var urlJson = await urlResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!urlResp.IsSuccessStatusCode)
            throw new VtException(ParseError(urlJson) ?? "upload_url alınamadı.");

        using var urlDoc = JsonDocument.Parse(urlJson);
        var uploadUrl = urlDoc.RootElement.GetProperty("data").GetString()
                        ?? throw new VtException("upload_url boş.");

        await using var stream = File.OpenRead(path);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = fileContent };
        req.Headers.Add("x-apikey", _config.ApiKey);

        using var uploadResp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var uploadJson = await uploadResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!uploadResp.IsSuccessStatusCode)
            throw new VtException(ParseError(uploadJson) ?? "Büyük dosya yüklemesi başarısız.");

        using var upDoc = JsonDocument.Parse(uploadJson);
        return upDoc.RootElement.GetProperty("data").GetProperty("id").GetString()
               ?? throw new VtException("Analiz kimliği alınamadı.");
    }

    private async Task<string?> PollAnalysisAsync(
        string analysisId,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(_config.UploadPollSeconds), ct).ConfigureAwait(false);

            await ThrottleAsync(ct).ConfigureAwait(false);
            using var resp = await SendWithRetryAsync(
                    HttpMethod.Get,
                    $"analyses/{analysisId}",
                    null,
                    ct)
                .ConfigureAwait(false);

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                continue;

            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("data").GetProperty("attributes")
                .GetProperty("status").GetString();

            progress?.Report($"Analiz durumu: {status}");

            if (status == "completed")
            {
                RecordRequest();
                if (doc.RootElement.GetProperty("data").GetProperty("attributes")
                    .TryGetProperty("meta", out var meta)
                    && meta.TryGetProperty("file_info", out var fi)
                    && fi.TryGetProperty("sha256", out var shaEl))
                {
                    return shaEl.GetString();
                }

                return doc.RootElement.GetProperty("meta")
                    .TryGetProperty("file_info", out var meta2)
                    && meta2.TryGetProperty("sha256", out var sha2)
                    ? sha2.GetString()
                    : null;
            }

            if (status is "failed" or "queued-expired")
                throw new VtException($"Analiz başarısız: {status}");
        }

        throw new VtException("Analiz zaman aşımı.");
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpMethod method,
        string relativeUrl,
        HttpContent? content,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var req = new HttpRequestMessage(method, relativeUrl) { Content = content };
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode != (HttpStatusCode)429)
                return resp;

            var retrySec = (int?)resp.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60;
            resp.Dispose();
            _logger.Warning("VT 429 — {Sec}s bekleniyor", retrySec);
            if (attempt == 2)
                throw new VtException($"Kota aşıldı — {retrySec}s sonra tekrar deneyin.", isQuota: true);

            await Task.Delay(TimeSpan.FromSeconds(retrySec), ct).ConfigureAwait(false);
        }

        throw new VtException("VT isteği başarısız.");
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ResetUsageIfNewDay();
            if (_usage.Count >= _config.DailyQuota)
                throw new VtException(
                    $"Günlük VirusTotal kotası doldu ({_config.DailyQuota}). Yarın tekrar deneyin.",
                    isQuota: true);

            var now = DateTime.Now;
            if (now < _nextAllowedAt)
            {
                var wait = _nextAllowedAt - now;
                _logger.Debug("VT throttle: {Wait}ms", wait.TotalMilliseconds);
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            _nextAllowedAt = DateTime.Now.AddMilliseconds(_config.MinRequestIntervalMs);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RecordRequest()
    {
        ResetUsageIfNewDay();
        _usage.Count++;
        _usage.Date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        SaveUsage();
    }

    private void ResetUsageIfNewDay()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_usage.Date != today)
        {
            _usage = new VtUsage { Date = today, Count = 0 };
            SaveUsage();
        }
    }

    private void Cache(string hash, VtReport report)
    {
        _cache[hash] = report;
        if (!string.IsNullOrEmpty(report.Sha256))
            _cache[report.Sha256] = report;
        SaveCache();
    }

    private VtReport ParseFileReport(string requestedHash, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var attrs = doc.RootElement.GetProperty("data").GetProperty("attributes");

        var stats = attrs.GetProperty("last_analysis_stats");
        var malicious = stats.GetProperty("malicious").GetInt32();
        var suspicious = stats.GetProperty("suspicious").GetInt32();
        var harmless = stats.GetProperty("harmless").GetInt32();
        var undetected = stats.GetProperty("undetected").GetInt32();

        var top = new List<VtEngineResult>();
        if (attrs.TryGetProperty("last_analysis_results", out var engines))
        {
            foreach (var eng in engines.EnumerateObject())
            {
                var cat = eng.Value.GetProperty("category").GetString() ?? "";
                if (cat is not ("malicious" or "suspicious"))
                    continue;

                top.Add(new VtEngineResult
                {
                    Engine = eng.Name,
                    Category = cat,
                    Result = eng.Value.GetProperty("result").GetString() ?? ""
                });
                if (top.Count >= 8)
                    break;
            }
        }

        var suggested = "";
        if (attrs.TryGetProperty("popular_threat_classification", out var ptc)
            && ptc.TryGetProperty("suggested_threat_label", out var stl))
        {
            suggested = stl.GetString() ?? "";
        }

        var verdict = ResolveVerdict(malicious, suspicious);

        return new VtReport
        {
            RequestedHash = requestedHash,
            Sha256 = attrs.TryGetProperty("sha256", out var s256) ? s256.GetString() ?? "" : "",
            Sha1 = attrs.TryGetProperty("sha1", out var s1) ? s1.GetString() ?? "" : "",
            Md5 = attrs.TryGetProperty("md5", out var md) ? md.GetString() ?? "" : "",
            MeaningfulName = attrs.TryGetProperty("meaningful_name", out var mn) ? mn.GetString() ?? "" : "",
            Malicious = malicious,
            Suspicious = suspicious,
            Harmless = harmless,
            Undetected = undetected,
            TopMatches = top,
            SuggestedLabel = suggested,
            LastAnalysisDate = attrs.TryGetProperty("last_analysis_date", out var lad)
                ? UnixToLocal(lad.GetInt64()) : null,
            LastSubmissionDate = attrs.TryGetProperty("last_submission_date", out var lsd)
                ? UnixToLocal(lsd.GetInt64()) : null,
            Verdict = verdict
        };
    }

    private VtVerdict ResolveVerdict(int malicious, int suspicious)
    {
        if (malicious > 0)
            return VtVerdict.Malicious;
        if (suspicious > 0)
            return VtVerdict.Suspicious;
        return VtVerdict.Clean;
    }

    private VtReport SimulateReport(string hash)
    {
        var bucket = hash.Length > 0 ? hash[0] % 5 : 0;
        return bucket switch
        {
            0 => BuildSim(hash, 14, 2, "trojan.banker/cerberus"),
            1 => BuildSim(hash, 3, 1, "adware.agent"),
            2 => BuildSim(hash, 0, 4, "pua.generic"),
            3 => BuildSim(hash, 0, 0, ""),
            _ => BuildSim(hash, 0, 0, "clean")
        };
    }

    private VtReport BuildSim(string hash, int malicious, int suspicious, string label)
    {
        List<VtEngineResult> engines;
        if (malicious > 0)
        {
            engines =
            [
                new VtEngineResult { Engine = "Kaspersky", Category = "malicious", Result = "HEUR:Trojan.AndroidOS" },
                new VtEngineResult { Engine = "ESET-NOD32", Category = "malicious", Result = label }
            ];
        }
        else if (suspicious > 0)
        {
            engines =
            [
                new VtEngineResult { Engine = "Sophos", Category = "suspicious", Result = "Generic PUA" }
            ];
        }
        else
        {
            engines = [];
        }

        var verdict = malicious > 0 ? VtVerdict.Malicious
            : suspicious > 0 ? VtVerdict.Suspicious
            : VtVerdict.Clean;

        return new VtReport
        {
            RequestedHash = hash,
            Sha256 = hash.Length == 64 ? hash : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(hash))).ToLowerInvariant(),
            MeaningfulName = "simulated_sample.apk",
            Malicious = malicious,
            Suspicious = suspicious,
            Harmless = Math.Max(0, 70 - malicious - suspicious),
            Undetected = Math.Max(0, 10 - suspicious),
            TopMatches = engines,
            SuggestedLabel = label,
            LastAnalysisDate = DateTime.Now,
            Verdict = verdict
        };
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var bytes = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(bytes);
    }

    private static DateTime UnixToLocal(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;

    private static string? ParseError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string NormalizeHash(string hash) =>
        hash.Trim().ToLowerInvariant();

    private static bool IsValidHashFormat(string hash) =>
        hash.Length is 32 or 40 or 64
        && hash.All(static c => Uri.IsHexDigit(c));

    private void ApplyApiKeyHeader()
    {
        _http.DefaultRequestHeaders.Remove("x-apikey");
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            _http.DefaultRequestHeaders.Add("x-apikey", _config.ApiKey.Trim());
    }

    private static VtConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<VtConfig>(File.ReadAllText(ConfigPath));
                if (cfg is not null)
                    return cfg;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "vt_config.json okunamadı");
        }

        return new VtConfig();
    }

    private Dictionary<string, VtReport> LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, VtReport>>(File.ReadAllText(CachePath));
                if (map is not null)
                    return new Dictionary<string, VtReport>(map, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "vt_cache.json okunamadı");
        }

        return new Dictionary<string, VtReport>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(SecurityDir);
            File.WriteAllText(CachePath,
                JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "vt_cache.json yazılamadı");
        }
    }

    private VtUsage LoadUsage()
    {
        try
        {
            if (File.Exists(UsagePath))
            {
                var u = JsonSerializer.Deserialize<VtUsage>(File.ReadAllText(UsagePath));
                if (u is not null)
                    return u;
            }
        }
        catch
        {
            // ignore
        }

        return new VtUsage { Date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
    }

    private void SaveUsage()
    {
        try
        {
            Directory.CreateDirectory(SecurityDir);
            File.WriteAllText(UsagePath, JsonSerializer.Serialize(_usage));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "vt_usage.json yazılamadı");
        }
    }

    private sealed class VtUsage
    {
        public string Date { get; set; } = "";
        public int Count { get; set; }
    }
}
