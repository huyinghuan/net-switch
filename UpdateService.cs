using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetSwitcher
{
    internal enum InstallationMode
    {
        Msi,
        Portable
    }

    internal sealed class InstallationInfo
    {
        public InstallationMode Mode { get; set; }
        public string ExecutablePath { get; set; } = "";
        public string DisplayName => Mode == InstallationMode.Msi ? "MSI 安装版" : "独立 EXE";
    }

    internal sealed class ReleaseAssetInfo
    {
        public string Name { get; set; } = "";
        public Uri DownloadUri { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
    }

    internal sealed class UpdateCheckResult
    {
        public bool Success { get; set; }
        public bool IsUpdateAvailable { get; set; }
        public string ErrorMessage { get; set; } = "";
        public Version CurrentVersion { get; set; }
        public Version LatestVersion { get; set; }
        public string TagName { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public Uri ReleasePage { get; set; }
        public InstallationInfo Installation { get; set; }
        public ReleaseAssetInfo PackageAsset { get; set; }
        public ReleaseAssetInfo ChecksumsAsset { get; set; }
    }

    internal sealed class UpdateDownloadResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = "";
        public string PackagePath { get; set; } = "";
    }

    internal static class UpdateService
    {
        private const string Owner = "huyinghuan";
        private const string Repository = "net-switch";
        private const long MaxReleaseJsonBytes = 2 * 1024 * 1024;
        private const long MaxPackageBytes = 250 * 1024 * 1024;
        private static readonly HttpClient Client = CreateClient();

        public static Version CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

        public static string CurrentVersionText => FormatVersion(CurrentVersion);

        public static InstallationInfo DetectInstallation()
        {
            var executable = Environment.ProcessPath ?? Application.ExecutablePath;
            executable = FullPath(executable);

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = root.OpenSubKey(@"SOFTWARE\NetSwitcher", false);
                    var mode = key?.GetValue("InstallMode") as string;
                    var installedPath = key?.GetValue("InstallPath") as string;
                    if (string.Equals(mode, "MSI", StringComparison.OrdinalIgnoreCase) &&
                        SamePath(executable, installedPath))
                    {
                        return new InstallationInfo
                        {
                            Mode = InstallationMode.Msi,
                            ExecutablePath = executable
                        };
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.WarningThrottled("update-install-detection",
                        "读取 MSI 安装标记失败：" + ex.Message, TimeSpan.FromMinutes(5));
                }
            }

            // 兼容加入安装标记之前发布的 MSI。安装器始终使用这个固定目录。
            var legacyMsiPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NetSwitcher", "NetSwitcher.exe");
            return new InstallationInfo
            {
                Mode = SamePath(executable, legacyMsiPath) ? InstallationMode.Msi : InstallationMode.Portable,
                ExecutablePath = executable
            };
        }

        public static async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = CurrentVersion,
                Installation = DetectInstallation()
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var endpoint = new Uri($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");
                using var response = await Client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    result.ErrorMessage = "GitHub 上还没有可用的正式 Release，或者仓库暂时无法公开访问。";
                    return result;
                }
                if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)
                {
                    result.ErrorMessage = "GitHub 暂时限制了更新查询，请稍后再试。";
                    return result;
                }
                if (!response.IsSuccessStatusCode)
                {
                    result.ErrorMessage = $"GitHub 返回了错误（HTTP {(int)response.StatusCode}）。";
                    return result;
                }
                if (response.Content.Headers.ContentLength > MaxReleaseJsonBytes)
                {
                    result.ErrorMessage = "GitHub 返回的版本信息异常，已停止处理。";
                    return result;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream,
                    new JsonDocumentOptions { MaxDepth = 32 }, timeout.Token).ConfigureAwait(false);
                var root = document.RootElement;
                result.TagName = GetString(root, "tag_name");
                if (!TryParseReleaseVersion(result.TagName, out var latest))
                {
                    result.ErrorMessage = "GitHub Release 的版本号格式无法识别：" + result.TagName;
                    return result;
                }

                result.LatestVersion = NormalizeVersion(latest);
                result.ReleaseNotes = GetString(root, "body");
                if (Uri.TryCreate(GetString(root, "html_url"), UriKind.Absolute, out var releasePage) &&
                    releasePage.Scheme == Uri.UriSchemeHttps)
                    result.ReleasePage = releasePage;

                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var assetNode in assets.EnumerateArray())
                    {
                        var asset = ParseAsset(assetNode);
                        if (asset == null) continue;
                        if (string.Equals(asset.Name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                            result.ChecksumsAsset = asset;

                        var expected = result.Installation.Mode == InstallationMode.Msi
                            ? $"NetSwitcher-Setup-{FormatVersion(latest)}-x64.msi"
                            : $"NetSwitcher-Portable-{FormatVersion(latest)}-x64.exe";
                        if (string.Equals(asset.Name, expected, StringComparison.OrdinalIgnoreCase))
                            result.PackageAsset = asset;
                    }
                }

                result.Success = true;
                result.IsUpdateAvailable = result.LatestVersion > NormalizeVersion(result.CurrentVersion);
                if (result.IsUpdateAvailable && result.PackageAsset == null)
                {
                    result.Success = false;
                    result.ErrorMessage = result.Installation.Mode == InstallationMode.Msi
                        ? "发现新版本，但 Release 中缺少对应的 MSI 安装包。"
                        : "发现新版本，但 Release 中缺少对应的独立 EXE。";
                }
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result.ErrorMessage = "连接 GitHub 超时，请检查网络或代理设置后重试。";
                return result;
            }
            catch (HttpRequestException ex)
            {
                result.ErrorMessage = "无法连接 GitHub，请检查网络、DNS 或代理设置。";
                AppLogger.Warning("检查更新网络请求失败：" + ex.Message);
                return result;
            }
            catch (JsonException ex)
            {
                result.ErrorMessage = "GitHub 返回的版本信息无法解析。";
                AppLogger.Error("解析 GitHub Release 信息失败。", ex);
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = "检查更新失败：" + ex.Message;
                AppLogger.Error("检查更新失败。", ex);
                return result;
            }
        }

        public static async Task<UpdateDownloadResult> DownloadUpdateAsync(
            UpdateCheckResult update, CancellationToken cancellationToken)
        {
            var result = new UpdateDownloadResult();
            if (update?.PackageAsset?.DownloadUri == null || update.LatestVersion == null)
            {
                result.ErrorMessage = "没有可下载的更新包。";
                return result;
            }

            var version = FormatVersion(update.LatestVersion);
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetSwitcher", "Updates", version);
            var destination = Path.Combine(directory, update.PackageAsset.Name);
            var partial = destination + ".download";

            try
            {
                Directory.CreateDirectory(directory);
                DeleteIfExists(partial);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                await DownloadFileAsync(update.PackageAsset.DownloadUri, partial,
                    update.PackageAsset.Size, timeout.Token).ConfigureAwait(false);

                var expectedHash = NormalizeDigest(update.PackageAsset.Sha256);
                if (string.IsNullOrEmpty(expectedHash) && update.ChecksumsAsset?.DownloadUri != null)
                {
                    expectedHash = await DownloadExpectedHashAsync(
                        update.ChecksumsAsset.DownloadUri,
                        update.PackageAsset.Name,
                        timeout.Token).ConfigureAwait(false);
                }
                if (string.IsNullOrEmpty(expectedHash))
                {
                    DeleteIfExists(partial);
                    result.ErrorMessage = "Release 未提供可用的 SHA-256 校验值，已拒绝安装。";
                    return result;
                }

                var actualHash = await ComputeSha256Async(partial, timeout.Token).ConfigureAwait(false);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteIfExists(partial);
                    result.ErrorMessage = "更新包校验失败，文件可能下载不完整，已停止更新。";
                    AppLogger.Error($"更新包 SHA-256 不匹配。期望 {expectedHash}，实际 {actualHash}。");
                    return result;
                }

                File.Move(partial, destination, true);
                result.Success = true;
                result.PackagePath = destination;
                AppLogger.Info($"更新包已下载并通过 SHA-256 校验：{destination}");
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                DeleteIfExists(partial);
                result.ErrorMessage = "下载更新超时，请稍后重试。";
                return result;
            }
            catch (HttpRequestException ex)
            {
                DeleteIfExists(partial);
                result.ErrorMessage = "下载更新失败，请检查网络后重试。";
                AppLogger.Warning("下载更新网络请求失败：" + ex.Message);
                return result;
            }
            catch (Exception ex)
            {
                DeleteIfExists(partial);
                result.ErrorMessage = "保存更新包失败：" + ex.Message;
                AppLogger.Error("下载或校验更新包失败。", ex);
                return result;
            }
        }

        public static bool StartUpdateHelper(
            InstallationInfo installation, string packagePath, out string error)
        {
            error = "";
            try
            {
                if (installation == null || !File.Exists(packagePath))
                {
                    error = "更新包不存在。";
                    return false;
                }

                var helperDirectory = Path.Combine(Path.GetDirectoryName(packagePath) ?? Path.GetTempPath(), "helper");
                Directory.CreateDirectory(helperDirectory);
                // 独立版的新 EXE 本身已经包含更新助手，直接运行可避免再复制一份约 60 MB 的文件。
                // MSI 更新包不能执行，因此 MSI 模式复制当前的小型框架依赖 EXE 作为临时助手。
                var helperPath = installation.Mode == InstallationMode.Portable
                    ? packagePath
                    : Path.Combine(helperDirectory, "NetSwitcher.UpdateHelper.exe");
                if (installation.Mode == InstallationMode.Msi)
                    File.Copy(installation.ExecutablePath, helperPath, true);

                var start = new ProcessStartInfo
                {
                    FileName = helperPath,
                    WorkingDirectory = helperDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add("--apply-update");
                start.ArgumentList.Add(installation.Mode == InstallationMode.Msi ? "msi" : "portable");
                start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                start.ArgumentList.Add(packagePath);
                start.ArgumentList.Add(installation.ExecutablePath);
                Process.Start(start);
                AppLogger.Info($"已启动更新助手，模式：{installation.DisplayName}。");
                return true;
            }
            catch (Exception ex)
            {
                error = "无法启动更新程序：" + ex.Message;
                AppLogger.Error("启动更新助手失败。", ex);
                return false;
            }
        }

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip |
                                         DecompressionMethods.Deflate |
                                         DecompressionMethods.Brotli
            };
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NetSwitcher-Update/" + CurrentVersionText);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
            return client;
        }

        private static ReleaseAssetInfo ParseAsset(JsonElement node)
        {
            var name = GetString(node, "name");
            var url = GetString(node, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                return null;

            var size = node.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var value)
                ? value
                : 0;
            return new ReleaseAssetInfo
            {
                Name = name,
                DownloadUri = uri,
                Size = size,
                Sha256 = GetString(node, "digest")
            };
        }

        private static async Task DownloadFileAsync(
            Uri uri, string destination, long advertisedSize, CancellationToken cancellationToken)
        {
            if (advertisedSize < 0 || advertisedSize > MaxPackageBytes)
                throw new InvalidDataException("更新包大小异常。");

            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > MaxPackageBytes)
                throw new InvalidDataException("更新包超过允许的最大大小。");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxPackageBytes) throw new InvalidDataException("更新包超过允许的最大大小。");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (advertisedSize > 0 && total != advertisedSize)
                throw new InvalidDataException("更新包大小与 Release 信息不一致。");
        }

        private static async Task<string> DownloadExpectedHashAsync(
            Uri checksumsUri, string packageName, CancellationToken cancellationToken)
        {
            using var response = await Client.GetAsync(checksumsUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 256 * 1024)
                throw new InvalidDataException("校验文件大小异常。");
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length < 66) continue;
                var hash = line.Substring(0, 64);
                var file = line.Substring(64).TrimStart(' ', '*');
                if (string.Equals(file, packageName, StringComparison.OrdinalIgnoreCase) && IsSha256(hash))
                    return hash.ToLowerInvariant();
            }
            return "";
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
                hash.AppendData(buffer, 0, read);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static string NormalizeDigest(string digest)
        {
            if (string.IsNullOrWhiteSpace(digest)) return "";
            var value = digest.Trim();
            if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                value = value.Substring("sha256:".Length);
            return IsSha256(value) ? value.ToLowerInvariant() : "";
        }

        private static bool IsSha256(string value) =>
            value?.Length == 64 && value.All(Uri.IsHexDigit);

        private static bool TryParseReleaseVersion(string tag, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag)) return false;
            var value = tag.Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            var suffix = value.IndexOfAny(new[] { '-', '+' });
            if (suffix >= 0) value = value.Substring(0, suffix);
            return Version.TryParse(value, out version) && version.Major >= 0;
        }

        private static Version NormalizeVersion(Version version) => new Version(
            Math.Max(0, version?.Major ?? 0),
            Math.Max(0, version?.Minor ?? 0),
            Math.Max(0, version?.Build ?? 0),
            Math.Max(0, version?.Revision ?? 0));

        public static string FormatVersion(Version version)
        {
            if (version == null) return "0.0.0";
            var build = Math.Max(0, version.Build);
            return version.Revision > 0
                ? $"{version.Major}.{version.Minor}.{build}.{version.Revision}"
                : $"{version.Major}.{version.Minor}.{build}";
        }

        private static string GetString(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        private static bool SamePath(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
            try
            {
                return string.Equals(FullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                    FullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string FullPath(string path) => Path.GetFullPath(path ?? "");

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 失败时由后续写入或移动操作给出更准确的错误。
            }
        }
    }

    internal static class UpdateInstaller
    {
        private const string UpdateArgument = "--apply-update";

        public static bool IsUpdateCommand(string[] args) =>
            args?.Length > 0 && string.Equals(args[0], UpdateArgument, StringComparison.OrdinalIgnoreCase);

        public static void Run(string[] args)
        {
            try
            {
                if (args == null || args.Length != 5 ||
                    !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
                    throw new InvalidDataException("更新助手参数无效。");

                var mode = args[1];
                var packagePath = Path.GetFullPath(args[3]);
                var targetPath = Path.GetFullPath(args[4]);
                WaitForOriginalProcess(processId);

                if (string.Equals(mode, "msi", StringComparison.OrdinalIgnoreCase))
                    ApplyMsi(packagePath, targetPath);
                else if (string.Equals(mode, "portable", StringComparison.OrdinalIgnoreCase))
                    ApplyPortable(packagePath, targetPath);
                else
                    throw new InvalidDataException("未知的更新模式。");
            }
            catch (Exception ex)
            {
                WriteUpdateLog("更新失败。", ex);
                MessageBox.Show(
                    "自动更新未能完成：\n" + ex.Message + "\n\n可以从 GitHub Release 手动下载安装。",
                    "NetSwitcher 更新失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static void WaitForOriginalProcess(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
                    throw new TimeoutException("等待旧版本退出超时。");
            }
            catch (ArgumentException)
            {
                // 原进程已经退出。
            }
        }

        private static void ApplyMsi(string packagePath, string targetPath)
        {
            if (!File.Exists(packagePath)) throw new FileNotFoundException("找不到 MSI 更新包。", packagePath);
            var logPath = Path.Combine(Path.GetDirectoryName(packagePath) ?? Path.GetTempPath(), "msi-update.log");
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                UseShellExecute = false
            };
            start.ArgumentList.Add("/i");
            start.ArgumentList.Add(packagePath);
            start.ArgumentList.Add("/passive");
            start.ArgumentList.Add("/norestart");
            start.ArgumentList.Add("/L*v");
            start.ArgumentList.Add(logPath);

            using var installer = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Windows Installer。");
            if (!installer.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
            {
                try { installer.Kill(true); } catch { }
                throw new TimeoutException("MSI 安装超过 10 分钟仍未完成。");
            }
            if (installer.ExitCode != 0 && installer.ExitCode != 3010 && installer.ExitCode != 1641)
                throw new InvalidOperationException(
                    $"Windows Installer 返回错误代码 {installer.ExitCode}。详细日志：{logPath}");

            WriteUpdateLog("MSI 更新安装成功。", null);
            RestartApplication(targetPath);
        }

        private static void ApplyPortable(string packagePath, string targetPath)
        {
            if (!File.Exists(packagePath)) throw new FileNotFoundException("找不到 EXE 更新包。", packagePath);
            var targetDirectory = Path.GetDirectoryName(targetPath) ??
                                  throw new InvalidDataException("无法确定程序目录。");
            Directory.CreateDirectory(targetDirectory);
            var staged = targetPath + ".update-new";
            var backup = targetPath + ".update-backup";
            DeleteIfExists(staged);
            DeleteIfExists(backup);
            File.Copy(packagePath, staged, true);

            try
            {
                File.Replace(staged, targetPath, backup, true);
            }
            catch (PlatformNotSupportedException)
            {
                ReplaceByMove(staged, targetPath, backup);
            }
            catch (IOException)
            {
                ReplaceByMove(staged, targetPath, backup);
            }

            DeleteIfExists(backup);
            WriteUpdateLog("独立 EXE 更新成功。", null);
            RestartApplication(targetPath);
        }

        private static void ReplaceByMove(string staged, string target, string backup)
        {
            File.Move(target, backup, true);
            try
            {
                File.Move(staged, target, true);
            }
            catch
            {
                if (File.Exists(backup)) File.Move(backup, target, true);
                throw;
            }
        }

        private static void RestartApplication(string targetPath)
        {
            if (!File.Exists(targetPath))
                throw new FileNotFoundException("更新完成，但找不到新版程序。", targetPath);
            Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 备份清理失败不影响已经完成的更新。
            }
        }

        private static void WriteUpdateLog(string message, Exception exception)
        {
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NetSwitcher", "Updates");
                Directory.CreateDirectory(directory);
                var text = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}";
                if (exception != null) text += exception + Environment.NewLine;
                File.AppendAllText(Path.Combine(directory, "update.log"), text, Encoding.UTF8);
            }
            catch
            {
                // 更新日志不能阻止错误提示。
            }
        }
    }
}
