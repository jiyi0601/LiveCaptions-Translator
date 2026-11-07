using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LiveCaptionsTranslator.speech;

public static class ModelInstaller
{
    public sealed record ModelFile(string Path, long Size, string Sha256);
    public sealed record Package(string Name, string Archive, string Url, long Size, string Sha256, ModelFile[] Files);
    public static Package[] Manifest()
    {
        using var resource = typeof(ModelInstaller).Assembly.GetManifestResourceStream("SpeakerModels.json")
            ?? throw new IOException("缺少模型清单。");
        return JsonSerializer.Deserialize<Package[]>(resource, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    private static async Task<bool> Matches(string path, string hash, long size, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
    public static async Task EnsureAsync(string root, Action<string> status, CancellationToken token)
    {
        Directory.CreateDirectory(root);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LiveCaptionsTranslator-Speaker/1.0");
        foreach (var package in Manifest())
        {
            bool valid = true;
            foreach (var file in package.Files)
                if (!await Matches(Path.Combine(root, file.Path), file.Sha256, file.Size, token)) { valid = false; break; }
            if (valid) continue;
            string archive = Path.Combine(root, package.Archive + ".download");
            if (!await Matches(archive, package.Sha256, package.Size, token))
            {
                status("首次下载" + package.Name + "模型 · 0%（全部约 558 MiB）");
                using var response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(token))
                await using (var output = File.Create(archive))
                {
                    byte[] bytes = new byte[128 * 1024];
                    long count = 0; int last = -1, n;
                    while ((n = await input.ReadAsync(bytes, token)) != 0)
                    {
                        await output.WriteAsync(bytes.AsMemory(0, n), token);
                        count += n;
                        int percent = (int)(count * 100 / package.Size);
                        if (percent != last) { last = percent; status("下载" + package.Name + "模型 · " + percent + "%"); }
                    }
                }
                if (!await Matches(archive, package.Sha256, package.Size, token))
                    throw new IOException("模型下载校验失败，请重新开启说话人模式重试。");
            }
            token.ThrowIfCancellationRequested();
            status("正在安装" + package.Name + "模型…");
            if (package.Archive.EndsWith(".tar.bz2", StringComparison.Ordinal))
            {
                var start = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add("-xf"); start.ArgumentList.Add(archive);
                start.ArgumentList.Add("-C"); start.ArgumentList.Add(root);
                // Extract only manifest-owned inference files, not the much larger fp32 alternatives.
                foreach (var file in package.Files) start.ArgumentList.Add(file.Path);
                using var process = Process.Start(start) ?? throw new IOException("无法启动模型解压程序。");
                var errors = process.StandardError.ReadToEndAsync(token);
                try { await process.WaitForExitAsync(token); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); throw; }
                if (process.ExitCode != 0) throw new IOException("模型解压失败：" + await errors);
            }
            else File.Copy(archive, Path.Combine(root, package.Files[0].Path), overwrite: true);
            foreach (var file in package.Files)
                if (!await Matches(Path.Combine(root, file.Path), file.Sha256, file.Size, token))
                    throw new IOException("模型文件校验失败：" + file.Path);
            File.Delete(archive);
        }
    }
}
