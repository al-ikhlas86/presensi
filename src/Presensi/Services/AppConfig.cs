using System.IO;
using System.Text.Json;
using Presensi.Logging;

namespace Presensi.Services;

/// <summary>
/// Konfigurasi per-PC kiosk - SENGAJA file JSON terpisah di sebelah .exe
/// (bukan ditanam di kode/appsettings tertanam) supaya token kiosk TIDAK
/// PERNAH ikut ke repo/hasil compile publik, dan supaya tiap PC sekolah
/// bisa diatur sendiri-sendiri tanpa build ulang (mis. beda BaseUrl kalau
/// suatu saat ada lingkungan staging).
/// </summary>
public sealed class AppConfig
{
    public string BaseUrl { get; set; } = "https://alikhlas86.duckdns.org/mobile-api";
    public string KioskToken { get; set; } = "";

    /// <summary>
    /// Format "HH:mm". Batas waktu MASUK -> otomatis dianggap PULANG mulai
    /// jam ini (menggantikan toggle manual Masuk/Pulang - lihat
    /// ScheduleService.GetCurrentMode()). JamMasuk sendiri murni informasi
    /// yang ditampilkan di UI, cutoff sesungguhnya pakai JamPulang saja.
    /// </summary>
    public string JamMasuk { get; set; } = "06:30";
    public string JamPulang { get; set; } = "15:00";

    /// <summary>
    /// "default" (1024x720, bisa diubah) | "bebas" (ukuran/posisi terakhir
    /// dipertahankan apa adanya, bawaan lama) | "fullscreen" | "4:3" | "16:9"
    /// | "1:1" (jendela rasio tetap, tidak resizable).
    /// </summary>
    public string DisplayMode { get; set; } = "default";

    /// <summary>
    /// Port COM scanner terakhir yang berhasil tersambung - dipakai utk
    /// otomatis konek lagi tiap app dibuka/PC dinyalakan, tanpa perlu klik
    /// "Sambungkan Scanner" manual tiap kali (diminta user 2026-09-01).
    /// </summary>
    public string? ScannerPort { get; set; }

    /// <summary>
    /// Panel "Presensi Terbaru" di kanan - opsional, bisa dimatikan (diminta
    /// user 2026-09-01, PC dgn layar sempit mungkin tidak perlu panel ini).
    /// </summary>
    public bool ShowRiwayatPanel { get; set; } = true;

    /// <summary>
    /// Fine-grained Personal Access Token GitHub, scope Contents:Read-only
    /// KHUSUS repo "presensi" - dipakai UpdateService.cs cek/unduh rilis
    /// terbaru lewat REST API krn repo ini privat (URL publik "releases/
    /// latest/download/..." SELALU 404 tanpa kredensial utk repo privat,
    /// dibuktikan langsung 2026-09-01). Kosong = cek update dilewati diam2.
    /// </summary>
    public string? GithubToken { get; set; }

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                var fresh = new AppConfig();
                Save(fresh);
                Log.Warn($"appsettings.json belum ada, dibuat baru dgn token KOSONG di {ConfigPath} - isi KioskToken sebelum presensi bisa berfungsi.");
                return fresh;
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json);
            return config ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Error("Gagal membaca appsettings.json, pakai default kosong", ex);
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            Log.Error("Gagal menyimpan appsettings.json", ex);
        }
    }
}
