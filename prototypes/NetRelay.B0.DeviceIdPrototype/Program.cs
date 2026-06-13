using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

return await FingerprintTestTool.RunAsync(args);

internal static class FingerprintTestTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] is "--help" or "-h" or "/?")
            {
                PrintHelp();
                return 0;
            }

            if (args.Length > 0 && args[0] == "--compare")
            {
                return await CompareAsync(args);
            }

            if (args.Length >= 2
                && string.Equals(Path.GetExtension(args[0]), ".json", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetExtension(args[1]), ".json", StringComparison.OrdinalIgnoreCase))
            {
                return await CompareAsync(["--compare", .. args]);
            }

            return await ExportAsync(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> ExportAsync(string[] args)
    {
        var output = GetOption(args, "--output")
            ?? Path.Combine(
                AppContext.BaseDirectory,
                "reports",
                $"fingerprint-report-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
        var label = GetOption(args, "--label") ?? "未填写";

        IDeviceFingerprintProvider provider = new CompositeDeviceFingerprintProvider(
        [
            new DeviceIdHardwareEvidenceSource(),
            new NetRelayNetworkEvidenceSource()
        ]);

        var stopwatch = Stopwatch.StartNew();
        var fingerprint = await provider.CollectAsync(CancellationToken.None);
        var report = FingerprintReport.Create(fingerprint, label, stopwatch.ElapsedMilliseconds);

        EnsureParentDirectory(output);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, JsonOptions));

        Console.WriteLine("NetRelay B0 设备指纹测试报告已生成。");
        Console.WriteLine($"路径: {Path.GetFullPath(output)}");
        Console.WriteLine($"标签: {report.Label}");
        Console.WriteLine($"证据分类: {report.Evidence.Count}");
        Console.WriteLine($"采集耗时: {report.ElapsedMilliseconds} ms");
        Console.WriteLine("报告不包含原始硬件标识，但包含稳定哈希，请勿公开分享。");
        return 0;
    }

    private static async Task<int> CompareAsync(string[] args)
    {
        if (args.Length < 3)
        {
            throw new ArgumentException("--compare 需要两个报告路径。");
        }

        var beforePath = args[1];
        var afterPath = args[2];
        var output = GetOption(args, "--output")
            ?? Path.Combine(
                AppContext.BaseDirectory,
                "reports",
                $"fingerprint-comparison-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");

        var before = await ReadReportAsync(beforePath);
        var after = await ReadReportAsync(afterPath);
        var comparison = FingerprintComparison.Create(before, after);

        EnsureParentDirectory(output);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(comparison, JsonOptions));

        Console.WriteLine("NetRelay B0 设备指纹报告比较完成。");
        Console.WriteLine($"路径: {Path.GetFullPath(output)}");
        Console.WriteLine($"核心硬件匹配分类: {comparison.CoreMatchedCategories}/{comparison.CoreComparedCategories}");
        Console.WriteLine($"网卡辅助匹配分类: {comparison.NetworkMatchedCategories}/{comparison.NetworkComparedCategories}");
        Console.WriteLine($"测试结论: {comparison.Summary}");
        return 0;
    }

    private static async Task<FingerprintReport> ReadReportAsync(string path)
    {
        var report = JsonSerializer.Deserialize<FingerprintReport>(
            await File.ReadAllTextAsync(path),
            JsonOptions);
        return report ?? throw new InvalidDataException($"报告无法解析: {path}");
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, argument => argument == name);
        if (index < 0)
        {
            return null;
        }

        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{name} 缺少参数值。");
        }

        return args[index + 1];
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("无法确定报告输出目录。");
        Directory.CreateDirectory(directory);
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            NetRelay B0 DeviceId 测试工具

            双击或不带参数运行：
              在 EXE 同目录 reports 文件夹生成脱敏指纹报告。

            生成带标签的报告：
              NetRelay.B0.DeviceIdTestTool.exe --label "更换网卡前"

            指定输出路径：
              NetRelay.B0.DeviceIdTestTool.exe --label "基准" --output D:\Reports\before.json

            比较两个报告：
              NetRelay.B0.DeviceIdTestTool.exe --compare before.json after.json

            也可以将两份 JSON 报告同时拖到 EXE 上进行比较。
            """);
    }
}

internal sealed record FingerprintReport(
    int ReportSchemaVersion,
    int FingerprintVersion,
    string ToolVersion,
    DateTimeOffset GeneratedAtUtc,
    string Label,
    string OperatingSystem,
    long ElapsedMilliseconds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Evidence)
{
    public static FingerprintReport Create(
        DeviceFingerprintEvidence fingerprint,
        string label,
        long elapsedMilliseconds) =>
        new(
            ReportSchemaVersion: 1,
            fingerprint.Version,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            DateTimeOffset.UtcNow,
            label,
            Environment.OSVersion.VersionString,
            elapsedMilliseconds,
            fingerprint.Evidence);
}

internal sealed record EvidenceCategoryComparison(
    string Category,
    int BeforeCount,
    int AfterCount,
    int MatchedCount,
    int AddedCount,
    int RemovedCount);

internal sealed record FingerprintComparison(
    int ComparisonSchemaVersion,
    DateTimeOffset ComparedAtUtc,
    string BeforeLabel,
    string AfterLabel,
    int CoreComparedCategories,
    int CoreMatchedCategories,
    int NetworkComparedCategories,
    int NetworkMatchedCategories,
    string Summary,
    IReadOnlyList<EvidenceCategoryComparison> Categories)
{
    public static FingerprintComparison Create(FingerprintReport before, FingerprintReport after)
    {
        var categoryNames = before.Evidence.Keys
            .Concat(after.Evidence.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var categories = categoryNames
            .Select(category => CompareCategory(category, before.Evidence, after.Evidence))
            .ToArray();

        var core = categories.Where(category => category.Category.StartsWith("hardware.", StringComparison.Ordinal)).ToArray();
        var network = categories.Where(category => category.Category.StartsWith("network.", StringComparison.Ordinal)).ToArray();
        var coreMatched = core.Count(category => category.MatchedCount > 0);
        var networkMatched = network.Count(category => category.MatchedCount > 0);

        var summary = coreMatched == core.Length && networkMatched == network.Length
            ? "同机短期证据完全一致"
            : coreMatched >= 2
                ? "核心硬件仍有较强匹配，环境或部分硬件发生变化"
                : "核心硬件匹配不足，不应自动认定为同一设备";

        return new FingerprintComparison(
            ComparisonSchemaVersion: 1,
            DateTimeOffset.UtcNow,
            before.Label,
            after.Label,
            core.Length,
            coreMatched,
            network.Length,
            networkMatched,
            summary,
            categories);
    }

    private static EvidenceCategoryComparison CompareCategory(
        string category,
        IReadOnlyDictionary<string, IReadOnlyList<string>> before,
        IReadOnlyDictionary<string, IReadOnlyList<string>> after)
    {
        var beforeValues = before.GetValueOrDefault(category, []).ToHashSet(StringComparer.Ordinal);
        var afterValues = after.GetValueOrDefault(category, []).ToHashSet(StringComparer.Ordinal);
        var matched = beforeValues.Intersect(afterValues, StringComparer.Ordinal).Count();

        return new EvidenceCategoryComparison(
            category,
            beforeValues.Count,
            afterValues.Count,
            matched,
            afterValues.Count - matched,
            beforeValues.Count - matched);
    }
}
