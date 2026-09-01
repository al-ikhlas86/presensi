using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Presensi.Logging;

namespace Presensi.Services;

/// <summary>
/// Auto-update lewat GitHub REST API (BUKAN URL "releases/latest/download/
/// ..." biasa) - repo "presensi" SENGAJA tetap PRIVAT (keputusan user
/// 2026-09-01), dan URL publik semacam itu TERBUKTI SELALU 404 utk repo
/// privat tanpa kredensial apa pun (dibuktikan langsung dari log nyata PC
/// uji, bukan asumsi). Token GitHub disimpan di appsettings.json
/// (GithubToken) - pola SAMA PERSIS dgn KioskToken, BUKAN ditanam di kode/
/// compiled exe supaya tidak bisa diambil lewat decompile. Harus dibuat
/// sbg Fine-grained PAT dgn scope SESEMPIT mungkin: cuma repo "presensi",
/// permission "Contents: Read-only" - kalau bocor pun cuma bisa baca 1
/// repo ini, tidak bisa apa-apa lagi ke akun/repo lain.
/// </summary>
public static class UpdateService
{
#if NET48
    private const string AssetName = "Presensi-net48.zip";
#else
    private const string AssetName = "Presensi-net8.zip";
#endif
    private const string ApiLatestReleaseUrl = "https://api.github.com/repos/al-ikhlas86/presensi/releases/latest";
    private const string ApiAssetUrlTemplate = "https://api.github.com/repos/al-ikhlas86/presensi/releases/assets/{0}";

    public static async Task CheckAndApplyAsync(AppConfig config)
    {
        try
        {
            var token = config.GithubToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                Log.Warn("[Update] GithubToken kosong di appsettings.json - cek update dilewati.");
                return;
            }

            var installed = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            Log.Info($"[Update] Cek rilis terbaru via GitHub API (versi terpasang {installed})");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // GitHub API MEWAJIBKAN User-Agent (request tanpa ini ditolak 403),
            // dan token dikirim via header Authorization standar - BUKAN query
            // string (supaya tidak ikut tercatat di log server/proxy mana pun).
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Presensi-AlIkhlas86-Updater");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var releaseJson = await http.GetStringAsync(ApiLatestReleaseUrl).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(releaseJson);
            var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var versionText = tagName.TrimStart('v', 'V');

            if (!Version.TryParse(NormalizeVersion(versionText), out var remote))
            {
                Log.Warn($"[Update] Tag rilis \"{tagName}\" tidak bisa dibaca sbg versi - dilewati.");
                return;
            }
            if (remote <= installed)
            {
                Log.Info($"[Update] Sudah versi terbaru (terpasang {installed}, rilis {remote}).");
                return;
            }

            long assetId = 0;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == AssetName)
                {
                    assetId = asset.GetProperty("id").GetInt64();
                    break;
                }
            }
            if (assetId == 0)
            {
                Log.Warn($"[Update] Rilis {tagName} tidak punya asset \"{AssetName}\" - dilewati.");
                return;
            }

            Log.Info($"[Update] Update ditemukan: {installed} -> {remote}. Mengunduh asset id={assetId}...");
            using var assetReq = new HttpRequestMessage(HttpMethod.Get, string.Format(ApiAssetUrlTemplate, assetId));
            // Accept ini WAJIB - tanpa ini GitHub API balikin metadata JSON
            // asset-nya, BUKAN isi filenya.
            assetReq.Headers.Accept.ParseAdd("application/octet-stream");
            using var assetResp = await http.SendAsync(assetReq).ConfigureAwait(false);
            assetResp.EnsureSuccessStatusCode();
            var zipBytes = await assetResp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            Log.Info($"[Update] Unduhan selesai ({zipBytes.Length:N0} bytes). Menyiapkan pemasangan...");

            ApplyAndRestart(zipBytes);
        }
        catch (Exception ex)
        {
            // Gagal cek/unduh update (mis. sekolah lagi tidak ada internet,
            // atau token GithubToken keliru/kedaluwarsa) TIDAK BOLEH
            // mengganggu fungsi utama kiosk - dicatat, dicoba lagi otomatis
            // di siklus berikutnya (lihat App.xaml.cs).
            Log.Error("[Update] Gagal cek/unduh update (diabaikan, aplikasi tetap jalan seperti biasa)", ex);
        }
    }

    // "1.1.0" -> "1.1.0.0" - System.Version butuh >=2 bagian, dibuat selalu 4
    // bagian supaya perbandingan dgn versi assembly (SELALU 4 bagian) konsisten.
    private static string NormalizeVersion(string v)
    {
        var parts = v.Trim().Split('.');
        var padded = parts.Concat(Enumerable.Repeat("0", Math.Max(0, 4 - parts.Length))).Take(4);
        return string.Join(".", padded);
    }

    private static void ApplyAndRestart(byte[] zipBytes)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var exePath = Path.Combine(installDir, "Presensi.exe");

        // Ekstrak ke folder SEMENTARA dulu (bukan langsung ke installDir) -
        // ini aman dilakukan SAAT APP MASIH JALAN krn tidak menyentuh file
        // yang sedang dikunci sama sekali. Baru dipindah ke installDir oleh
        // helper cmd.exe SETELAH app benar2 tertutup di bawah.
        var stagingDir = Path.Combine(Path.GetTempPath(), "presensi-update-" + Guid.NewGuid().ToString("N"));
        var zipPath = stagingDir + ".zip";
        Directory.CreateDirectory(stagingDir);
        File.WriteAllBytes(zipPath, zipBytes);
        // Overload 2-argumen (bukan yg py "overwriteFiles") SENGAJA dipakai -
        // stagingDir baru dibuat via Guid.NewGuid() di atas, PASTI kosong,
        // jadi overwrite tidak relevan. Overload 3-argumen itu juga tidak ada
        // di assembly System.IO.Compression.FileSystem klasik net48.
        ZipFile.ExtractToDirectory(zipPath, stagingDir);
        File.Delete(zipPath);
        Log.Info($"[Update] Berhasil diekstrak ke folder sementara: {stagingDir}");

        var pid = Process.GetCurrentProcess().Id;
        var scriptPath = Path.Combine(Path.GetTempPath(), "presensi-update.bat");
        var script =
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL\r\n" +
            "if not errorlevel 1 (\r\n" +
            "    ping 127.0.0.1 -n 2 >NUL\r\n" +
            "    goto wait\r\n" +
            ")\r\n" +
            $"xcopy \"{stagingDir}\\*\" \"{installDir}\\\" /Y /E /I >NUL\r\n" +
            $"rmdir /S /Q \"{stagingDir}\"\r\n" +
            $"start \"\" \"{exePath}\"\r\n" +
            "del \"%~f0\"\r\n";
        File.WriteAllText(scriptPath, script);
        Log.Info("[Update] Helper penutup+pasang disiapkan - aplikasi akan tertutup sebentar lalu terbuka lagi otomatis dgn versi baru.");

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        // Tutup app SEKARANG - helper di atas sudah menunggu PID ini keluar
        // (via tasklist) sebelum menimpa file.
        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }
}
