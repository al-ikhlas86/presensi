using System.Reflection;

namespace Presensi.Services;

/// <summary>
/// Versi aplikasi yang sedang berjalan (dicap dari tag rilis saat build, mis. "1.5.0").
/// Ditampilkan di jendela utama dan dikirim ke server (header X-App-Version) supaya Admin IT
/// bisa melihat dari jauh kiosk mana yang sudah ter-update.
/// </summary>
public static class AppVersionInfo
{
    public static string Current { get; } = Baca();

    private static string Baca()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        // Build lokal/uji (0.0.0.0) ditampilkan apa adanya; rilis resmi "1.5.0.0" -> "1.5.0".
        return v is null ? "?" : $"{v.Major}.{v.Minor}.{System.Math.Max(v.Build, 0)}";
    }
}
