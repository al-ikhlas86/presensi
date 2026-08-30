using System.IO;
using System.Text;

namespace Presensi.Logging;

/// <summary>
/// Logger file sederhana, tanpa dependency eksternal (sengaja - kiosk ini
/// harus tetap jalan walau paket NuGet tertentu bermasalah). 1 file per
/// hari di %LocalAppData%\Presensi\logs, rotasi otomatis: file lebih tua
/// dari 30 hari dihapus saat aplikasi start (kiosk jalan berbulan-bulan
/// tanpa reinstall, jangan sampai log menumpuk tak terbatas).
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Presensi", "logs");

    static Log()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            PurgeOldLogs();
        }
        catch
        {
            // Logging gagal TIDAK BOLEH menjatuhkan aplikasi kiosk - diam saja.
        }
    }

    private static void PurgeOldLogs()
    {
        var cutoff = DateTime.Now.AddDays(-30);
        foreach (var file in Directory.EnumerateFiles(LogDir, "*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff)
            {
                try { File.Delete(file); } catch { /* abaikan, bukan fatal */ }
            }
        }
    }

    private static string CurrentFile => Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        lock (Gate)
        {
            try { File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* kiosk tetap jalan meski disk penuh/terkunci */ }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");
}
