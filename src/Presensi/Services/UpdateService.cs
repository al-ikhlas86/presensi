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
/// Auto-update lewat GitHub REST API. TANPA token/Authorization header
/// (2026-09-12, repo "presensi" diubah jadi PUBLIC supaya CI/CD tidak lagi
/// kena limit storage GitHub Actions - Artifacts/Cache selalu gratis tak
/// terbatas utk repo public, beda dari repo privat yang punya kuota kecil).
/// Endpoint REST API GitHub (metadata rilis maupun unduh asset lewat
/// /releases/assets/{id}) bisa diakses siapa saja tanpa kredensial apa pun
/// kalau repo-nya public. GithubToken di AppConfig (appsettings.json) SUDAH
/// TIDAK DIPAKAI di sini lagi - dibiarkan ada di AppConfig (bukan dihapus)
/// murni supaya PC yang appsettings.json-nya kebetulan masih punya baris itu
/// tidak error, TAPI tidak lagi punya efek apa pun ke proses update.
/// </summary>
public static class UpdateService
{
    // Diamati MainWindow utk tampilkan banner "jangan tutup aplikasi" -
    // ditambahkan 2026-09-01 setelah user berkali-kali menutup app di
    // tengah unduhan (114MB tidak instan, TIDAK ADA tanda visual apa pun
    // sebelumnya kalau app sedang mengunduh update, jadi wajar dikira
    // "tidak terjadi apa-apa" lalu ditutup - unduhan hangus, mulai dari nol
    // lagi tiap dicoba). Null/kosong = sembunyikan banner.
    public static event Action<string?>? StatusChanged;

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
            var installed = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            Log.Info($"[Update] Cek rilis terbaru via GitHub API (versi terpasang {installed})");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // GitHub API MEWAJIBKAN User-Agent (request tanpa ini ditolak 403).
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Presensi-AlIkhlas86-Updater");
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
            long assetSize = 0;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == AssetName)
                {
                    assetId = asset.GetProperty("id").GetInt64();
                    assetSize = asset.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
                    break;
                }
            }
            if (assetId == 0)
            {
                Log.Warn($"[Update] Rilis {tagName} tidak punya asset \"{AssetName}\" - dilewati.");
                return;
            }

            Log.Info($"[Update] Update ditemukan: {installed} -> {remote}. Mengunduh asset id={assetId}...");
            var sizeMb = assetSize > 0 ? $"{assetSize / 1024.0 / 1024.0:F0} MB" : "ukuran tidak diketahui";
            StatusChanged?.Invoke($"Memperbarui ke versi {remote} ({sizeMb}) - JANGAN TUTUP APLIKASI INI sampai selesai...");

            using var assetReq = new HttpRequestMessage(HttpMethod.Get, string.Format(ApiAssetUrlTemplate, assetId));
            // Accept ini WAJIB - tanpa ini GitHub API balikin metadata JSON
            // asset-nya, BUKAN isi filenya.
            assetReq.Headers.Accept.ParseAdd("application/octet-stream");
            using var assetResp = await http.SendAsync(assetReq).ConfigureAwait(false);
            assetResp.EnsureSuccessStatusCode();
            var zipBytes = await assetResp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            Log.Info($"[Update] Unduhan selesai ({zipBytes.Length:N0} bytes). Menyiapkan pemasangan...");
            StatusChanged?.Invoke("Update selesai diunduh - aplikasi akan tertutup sebentar lalu terbuka lagi otomatis...");

            ApplyAndRestart(zipBytes);
        }
        catch (Exception ex)
        {
            // Gagal cek/unduh update (mis. sekolah lagi tidak ada internet,
            // atau token GithubToken keliru/kedaluwarsa, ATAU aplikasi
            // ditutup paksa di tengah unduhan) TIDAK BOLEH mengganggu fungsi
            // utama kiosk - dicatat, dicoba lagi otomatis di siklus
            // berikutnya (lihat App.xaml.cs). Banner disembunyikan lagi -
            // JANGAN dibiarkan nyangkut "sedang mengunduh" kalau ternyata gagal.
            StatusChanged?.Invoke(null);
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
