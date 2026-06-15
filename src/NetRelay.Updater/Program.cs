using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using NetRelay.Contracts;
using NetRelay.Contracts.Security;

namespace NetRelay.Updater;

public static class Program
{
    private static string _logFilePath = string.Empty;
    private static readonly string RootPublicKey = OperationalKeyCertificate.DefaultRootPublicKeyBase64;

    public static int Main(string[] args)
    {
        InitializeLogFile();
        Log("=========================================");
        Log($"独立更新器启动，命令行参数: {string.Join(" ", args)}");

        // 1. 参数解析
        string? packagePath = null;
        string? manifestPath = null;
        string? targetDir = null;
        int parentPid = -1;
        string? executable = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--package", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                packagePath = args[++i];
            }
            else if (string.Equals(args[i], "--manifest", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                manifestPath = args[++i];
            }
            else if (string.Equals(args[i], "--target-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                targetDir = args[++i];
            }
            else if (string.Equals(args[i], "--parent-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var pid))
                {
                    parentPid = pid;
                }
            }
            else if (string.Equals(args[i], "--executable", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                executable = args[++i];
            }
        }

        RecoverLegacyMalformedTargetArguments(args, ref targetDir, ref parentPid, ref executable);

        if (string.IsNullOrEmpty(packagePath) || string.IsNullOrEmpty(manifestPath) || string.IsNullOrEmpty(targetDir) || parentPid == -1 || string.IsNullOrEmpty(executable))
        {
            Log("错误: 缺失必要参数 (--package, --manifest, --target-dir, --parent-pid, --executable)。");
            return 1;
        }

        var backupMap = new List<(string Source, string Backup)>();
        var installedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 2. 等待父进程退出
            Log($"等待父进程 (PID: {parentPid}) 退出...");
            try
            {
                var parentProcess = Process.GetProcessById(parentPid);
                if (!parentProcess.WaitForExit(10000))
                {
                    Log("警告: 父进程在 10 秒内未退出，正在强制终止...");
                    parentProcess.Kill(entireProcessTree: true);
                }
            }
            catch (ArgumentException)
            {
                Log("父进程已提前退出。");
            }

            // 3. 安全性自检：验证服务端签名清单及更新 ZIP 哈希
            Log("开始验证更新清单签名和更新包哈希...");
            if (!File.Exists(packagePath))
            {
                throw new FileNotFoundException($"找不到更新包文件: {packagePath}");
            }

            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException($"找不到更新清单文件: {manifestPath}");
            }

            var manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
            var checkResponse = JsonSerializer.Deserialize<UpdateCheckResponse>(manifestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("无法反序列化更新清单。");
            var now = DateTimeOffset.UtcNow;
            using var rootKey = ECDsa.Create();
            rootKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(RootPublicKey), out _);

            if (!checkResponse.Certificate.Verify(now, rootKey, "update-manifest"))
            {
                throw new CryptographicException("更新清单证书链验证失败。");
            }

            using var operationalKey = ECDsa.Create();
            operationalKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(checkResponse.Certificate.PublicKey), out _);

            if (!checkResponse.Envelope.Verify("update-manifest", checkResponse.Envelope.Nonce, now, operationalKey))
            {
                throw new CryptographicException("更新清单签名校验失败。");
            }

            var manifest = JsonSerializer.Deserialize<UpdateManifest>(checkResponse.Envelope.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("更新清单载荷损坏。");
            var packageInfo = new FileInfo(packagePath);
            if (manifest.PackageSize != packageInfo.Length)
            {
                throw new InvalidDataException($"更新包大小不匹配。预期: {manifest.PackageSize}，实际: {packageInfo.Length}");
            }

            using (var packageStream = OpenPackageWithRetry(packagePath))
            {
                var packageHash = Convert.ToHexString(SHA256.HashData(packageStream)).ToLowerInvariant();
                if (!string.Equals(packageHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CryptographicException($"更新包哈希不匹配。预期: {manifest.Sha256}，实际: {packageHash}");
                }
            }

            Log($"签名和哈希验证成功。目标版本: v{manifest.Version}");

            // 4. 备份旧文件
            Log("正在准备文件备份...");
            var backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetRelay", "updates", "backup-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(backupRoot);

            var filesToBackup = Directory.GetFiles(targetDir, "*.*", SearchOption.AllDirectories);
            foreach (var file in filesToBackup)
            {
                var relativePath = Path.GetRelativePath(targetDir, file);
                // 排除日志文件夹与用户配置 config.json
                if (relativePath.StartsWith("logs", StringComparison.OrdinalIgnoreCase) || 
                    string.Equals(relativePath, "config.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var backupPath = Path.Combine(backupRoot, relativePath);
                var backupDir = Path.GetDirectoryName(backupPath);
                if (!string.IsNullOrEmpty(backupDir))
                {
                    Directory.CreateDirectory(backupDir);
                }

                File.Copy(file, backupPath, overwrite: true);
                backupMap.Add((file, backupPath));
            }
            Log($"备份完成。共备份了 {backupMap.Count} 个文件，备份目录: {backupRoot}");

            // 5. 解压覆盖写入文件
            Log("正在解压覆盖新版本文件...");
            using (var packageStream = OpenPackageWithRetry(packagePath))
            using (var zip = new ZipArchive(packageStream, ZipArchiveMode.Read))
            {
                var normalizedTargetDir = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    var destinationPath = Path.GetFullPath(Path.Combine(normalizedTargetDir, entry.FullName));
                    if (!destinationPath.StartsWith(normalizedTargetDir, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("解压路径越界。");
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        // 是目录
                        Directory.CreateDirectory(destinationPath);
                    }
                    else
                    {
                        // 是文件
                        var parentDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }
                        installedFiles.Add(destinationPath);
                        entry.ExtractToFile(destinationPath, overwrite: true);
                    }
                }
            }
            Log("文件解压覆盖成功。");

            // 6. 验证新版本
            Log("正在对新版本进行运行自检...");
            var verifyExePath = Path.Combine(targetDir, executable);
            if (!File.Exists(verifyExePath))
            {
                throw new FileNotFoundException($"未找到新版本主程序文件: {verifyExePath}");
            }

            var verifyStartInfo = new ProcessStartInfo
            {
                FileName = verifyExePath,
                Arguments = "--verify-update",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            bool verifySuccess = false;
            try
            {
                using var verifyProcess = Process.Start(verifyStartInfo);
                if (verifyProcess != null)
                {
                    if (verifyProcess.WaitForExit(8000)) // 8秒内等待退出码
                    {
                        if (verifyProcess.ExitCode == 0)
                        {
                            verifySuccess = true;
                        }
                        else
                        {
                            Log($"自检进程退出，但返回了错误代码: {verifyProcess.ExitCode}");
                        }
                    }
                    else
                    {
                        Log("错误: 自检进程启动超时（8 秒内无响应）。");
                        try { verifyProcess.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"拉起新版本自检发生异常: {ex.Message}");
            }

            if (verifySuccess)
            {
                Log("新版本自检通过！正在清理备份并启动主程序...");
                CleanupObsoleteRuntimeFiles(targetDir, installedFiles);

                // 清理备份
                try
                {
                    Directory.Delete(backupRoot, recursive: true);
                }
                catch (Exception ex)
                {
                    Log($"警告: 清理备份目录失败: {ex.Message}");
                }

                // 启动新版本
                Process.Start(new ProcessStartInfo
                {
                    FileName = verifyExePath,
                    UseShellExecute = true
                });

                Log("更新完成，独立更新器退出。");
                return 0;
            }
            else
            {
                // 自检失败，执行回滚
                Log("警告: 新版本自检失败！正在执行文件自动回滚恢复...");
                Rollback(backupMap, installedFiles);

                // 启动旧版本
                Process.Start(new ProcessStartInfo
                {
                    FileName = verifyExePath,
                    UseShellExecute = true
                });

                Log("回滚已完成，原旧版本已被成功拉起。更新器退出。");
                return 2;
            }
        }
        catch (Exception ex)
        {
            Log($"严重错误: 更新过程中发生异常: {ex.Message}\n{ex.StackTrace}");
            if (backupMap.Count > 0)
            {
                Log("正在回滚异常发生前已修改的文件...");
                Rollback(backupMap, installedFiles);
            }
            return 3;
        }
    }

    private static void RecoverLegacyMalformedTargetArguments(
        IReadOnlyList<string> args,
        ref string? targetDir,
        ref int parentPid,
        ref string? executable)
    {
        if (string.IsNullOrWhiteSpace(targetDir) || (parentPid != -1 && !string.IsNullOrWhiteSpace(executable)))
        {
            return;
        }

        var targetIndex = -1;
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--target-dir", StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = index;
                break;
            }
        }

        var malformedTail = targetIndex >= 0 && targetIndex + 1 < args.Count
            ? string.Join(" ", args.Skip(targetIndex + 1))
            : targetDir;
        var match = Regex.Match(
            malformedTail,
            "^(?<target>.+?)\"?\\s+--parent-pid\\s+(?<pid>\\d+)\\s+--executable\\s+(?<executable>.+)$",
            RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["pid"].Value, out var recoveredPid))
        {
            return;
        }

        targetDir = match.Groups["target"].Value.TrimEnd('"');
        parentPid = recoveredPid;
        executable = match.Groups["executable"].Value.Trim().Trim('"');
        Log("检测到旧客户端产生的畸形更新器参数，已完成兼容恢复。");
    }

    private static void CleanupObsoleteRuntimeFiles(
        string targetDir,
        HashSet<string> installedFiles)
    {
        var normalizedTargetDir = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var deletedFiles = 0;
        foreach (var file in Directory.GetFiles(normalizedTargetDir, "*.*", SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(file);
            if (installedFiles.Contains(fullPath) || !fullPath.StartsWith(normalizedTargetDir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(normalizedTargetDir, fullPath);
            if (!IsObsoleteRuntimeFile(relativePath))
            {
                continue;
            }

            try
            {
                File.Delete(fullPath);
                deletedFiles++;
            }
            catch (Exception ex)
            {
                Log($"警告: 删除旧版运行库文件失败: {relativePath}，错误: {ex.Message}");
            }
        }

        var deletedDirs = 0;
        foreach (var directory in Directory.GetDirectories(normalizedTargetDir, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
        {
            var relativePath = Path.GetRelativePath(normalizedTargetDir, directory);
            if (!IsObsoleteRuntimeDirectory(relativePath))
            {
                continue;
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    deletedDirs++;
                }
            }
            catch (Exception ex)
            {
                Log($"警告: 删除旧版空目录失败: {relativePath}，错误: {ex.Message}");
            }
        }

        Log($"旧版散文件清理完成。删除文件 {deletedFiles} 个，删除空目录 {deletedDirs} 个。");
    }

    private static bool IsObsoleteRuntimeFile(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        if (fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || relativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
            || relativePath.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
            || relativePath.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "createdump.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsObsoleteRuntimeDirectory(string relativePath)
    {
        var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return firstSegment is "cs" or "de" or "es" or "fr" or "it" or "ja" or "ko" or "pl" or "pt-BR" or "ru" or "tr" or "zh-Hans" or "zh-Hant";
    }

    private static FileStream OpenPackageWithRetry(string packagePath)
    {
        const int maxAttempts = 30;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                if (attempt == 1)
                {
                    Log($"更新包暂时被占用，正在等待文件释放: {ex.Message}");
                }
                Thread.Sleep(500);
            }
        }

        throw new IOException($"在等待文件释放后仍无法读取更新包: {packagePath}");
    }

    private static void Rollback(
        List<(string Source, string Backup)> backupMap,
        HashSet<string> installedFiles)
    {
        var originalFiles = backupMap
            .Select(pair => pair.Source)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var installedFile in installedFiles.Where(path => !originalFiles.Contains(path)))
        {
            try
            {
                if (File.Exists(installedFile))
                {
                    File.Delete(installedFile);
                }
            }
            catch (Exception ex)
            {
                Log($"删除更新新增文件失败: {installedFile}，错误: {ex.Message}");
            }
        }

        foreach (var pair in backupMap)
        {
            try
            {
                if (File.Exists(pair.Backup))
                {
                    var dir = Path.GetDirectoryName(pair.Source);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.Copy(pair.Backup, pair.Source, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Log($"回滚文件失败: {pair.Source} 从 {pair.Backup}，错误: {ex.Message}");
            }
        }
    }

    private static void InitializeLogFile()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logDir = Path.Combine(appData, "NetRelay", "logs");
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, $"updater-{DateTimeOffset.UtcNow:yyyy-MM-dd}.log");
        }
        catch
        {
            // Fallback to local execution directory if AppData fails
            _logFilePath = "updater.log";
        }
    }

    private static void Log(string message)
    {
        var logLine = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        Console.WriteLine(logLine);
        try
        {
            File.AppendAllText(_logFilePath, logLine + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Ignore logging file write errors
        }
    }
}
