using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;
using Presensi.Logging;

namespace Presensi.Services;

/// <summary>
/// Auto-update ditulis sendiri (BUKAN lagi pakai AutoUpdater.NET.Official) -
/// paket itu sempat dicoba (v1.9.3, mode zip) tapi diuji langsung 2026-09-01
/// di PC uji "D:\Z Test Absen": zip update berhasil diunduh, TAPI
/// Presensi.exe/.dll yang sedang berjalan tidak ikut tertimpa, dan TIDAK ADA
/// log/error apa pun yang bisa dipakai mendiagnosis kenapa - library itu
/// benar2 diam soal proses internalnya. Windows memang TIDAK MENGIZINKAN
/// menimpa .exe/.dll yang sedang dikunci proses yang menjalankannya -
/// implementasi di bawah ini menangani itu SECARA EKSPLISIT (tutup app dulu
/// via proses cmd.exe terpisah, baru timpa file, baru buka lagi), dan setiap
/// langkah dicatat ke Log supaya kalau gagal lagi, ada jejaknya.
///
/// SENGAJA pakai cmd.exe batch polos (bukan PowerShell) utk proses tunggu+
/// salin file - PowerShell modern (Expand-Archive, Wait-Process -Timeout)
/// butuh WMF 5+ yang belum tentu ada di Windows 7 lawas (target net48 app
/// ini). tasklist/find/xcopy adalah perintah cmd.exe yang sudah ada sejak
/// Windows 2000, jadi aman di KEDUA target tanpa pengecualian.
/// </summary>
public static class UpdateService
{
#if NET48
    private const string ManifestUrl = "https://github.com/al-ikhlas86/presensi/releases/latest/download/update-net48.xml";
#else
    private const string ManifestUrl = "https://github.com/al-ikhlas86/presensi/releases/latest/download/update-net8.xml";
#endif

    public static async Task CheckAndApplyAsync()
    {
        try
        {
            var installed = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            Log.Info($"[Update] Cek manifest {ManifestUrl} (versi terpasang {installed})");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // GitHub redirect "releases/latest/download/..." ke URL asset
            // sesungguhnya - HttpClient ikut redirect otomatis (bawaan).
            var xml = await http.GetStringAsync(ManifestUrl).ConfigureAwait(false);
            var root = XDocument.Parse(xml).Root;
            var versionText = root?.Element("version")?.Value;
            var url = root?.Element("url")?.Value;
            if (string.IsNullOrWhiteSpace(versionText) || string.IsNullOrWhiteSpace(url))
            {
                Log.Warn("[Update] Manifest tidak lengkap (elemen version/url kosong) - dilewati.");
                return;
            }

            // "!" krn net48 belum kenal anotasi [NotNullWhen] di IsNullOrWhiteSpace
            // (lihat catatan serupa di MainWindow.xaml.cs) - bukan bug.
            if (!Version.TryParse(NormalizeVersion(versionText!), out var remote))
            {
                Log.Warn($"[Update] Versi di manifest \"{versionText}\" tidak bisa dibaca - dilewati.");
                return;
            }

            if (remote <= installed)
            {
                Log.Info($"[Update] Sudah versi terbaru (terpasang {installed}, manifest {remote}).");
                return;
            }

            Log.Info($"[Update] Update ditemukan: {installed} -> {remote}. Mengunduh {url}");
            var zipBytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            Log.Info($"[Update] Unduhan selesai ({zipBytes.Length:N0} bytes). Menyiapkan pemasangan...");

            ApplyAndRestart(zipBytes);
        }
        catch (Exception ex)
        {
            // Gagal cek/unduh update (mis. sekolah lagi tidak ada internet)
            // TIDAK BOLEH mengganggu fungsi utama kiosk - dicatat, dicoba
            // lagi otomatis di siklus berikutnya (lihat App.xaml.cs).
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
        // (via tasklist) sebelum menimpa file, jadi urutan ini aman.
        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }
}
