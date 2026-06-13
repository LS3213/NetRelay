using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        string? targetDir = null;
        int parentPid = -1;
        string? executable = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--package", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                packagePath = args[++i];
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

        if (string.IsNullOrEmpty(packagePath) || string.IsNullOrEmpty(targetDir) || parentPid == -1 || string.IsNullOrEmpty(executable))
        {
            Log("错误: 缺失必要参数 (--package, --target-dir, --parent-pid, --executable)。");
            return 1;
        }

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

            // 3. 安全性自检：解压并验证 zip 包里的 manifest.json 签名
            Log("开始验证更新包签名...");
            if (!File.Exists(packagePath))
            {
                throw new FileNotFoundException($"找不到更新包文件: {packagePath}");
            }

            UpdateManifest manifest;
            using (var zip = ZipFile.OpenRead(packagePath))
            {
                var manifestEntry = zip.GetEntry("manifest.json");
                if (manifestEntry == null)
                {
                    throw new FileNotFoundException("更新包内未找到 manifest.json 配置文件。");
                }

                using var stream = manifestEntry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var manifestJson = reader.ReadToEnd();
                var checkResponse = JsonSerializer.Deserialize<UpdateCheckResponse>(manifestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (checkResponse == null)
                {
                    throw new InvalidDataException("无法反序列化更新包内的 manifest.json。");
                }

                // 验签
                var now = DateTimeOffset.UtcNow;
                using var rootKey = ECDsa.Create();
                rootKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(RootPublicKey), out _);

                if (!checkResponse.Certificate.Verify(now, rootKey, "update-manifest"))
                {
                    throw new CryptographicException("更新包内置证书链验证失败。");
                }

                using var operationalKey = ECDsa.Create();
                operationalKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(checkResponse.Certificate.PublicKey), out _);

                if (!checkResponse.Envelope.Verify("update-manifest", checkResponse.Envelope.Nonce, now, operationalKey))
                {
                    throw new CryptographicException("更新包签名校验失败，文件可能已被篡改。");
                }

                manifest = JsonSerializer.Deserialize<UpdateManifest>(checkResponse.Envelope.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("更新包内的清单载荷损坏。");
            }
            Log($"签名验证成功。目标版本: v{manifest.Version}");

            // 4. 备份旧文件
            Log("正在准备文件备份...");
            var backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetRelay", "updates", "backup-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(backupRoot);

            var filesToBackup = Directory.GetFiles(targetDir, "*.*", SearchOption.AllDirectories);
            var backupMap = new List<(string Source, string Backup)>();

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
            using (var zip = ZipFile.OpenRead(packagePath))
            {
                foreach (var entry in zip.Entries)
                {
                    // 排除 manifest.json 避免它释放到目标目录
                    if (string.Equals(entry.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var destinationPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                    if (!destinationPath.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase))
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
                Rollback(backupMap);

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
            // 如果已经备份了文件，进行万能回滚
            return 3;
        }
    }

    private static void Rollback(List<(string Source, string Backup)> backupMap)
    {
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
