namespace Presensi.Services;

/// <summary>
/// Ganti toggle manual "Masuk"/"Pulang" - user cuma atur 1x jam cutoff
/// lewat SettingsWindow, sistem otomatis pilih mode sesuai jam berjalan di
/// PC. Cutoff sesungguhnya pakai JamPulang saja (sebelum jam itu = Masuk,
/// jam itu ke atas = Pulang) - JamMasuk murni informasi yang ditampilkan
/// ke user, bukan dipakai hitung (sekolah cuma butuh 1 titik pindah mode
/// per hari, bukan jendela waktu terpisah).
/// </summary>
public static class ScheduleService
{
    public static AttendanceMode GetCurrentMode(AppConfig config) =>
        GetCurrentMode(config, DateTime.Now.TimeOfDay);

    public static AttendanceMode GetCurrentMode(AppConfig config, TimeSpan now)
    {
        var cutoff = ParseOrDefault(config.JamPulang, new TimeSpan(15, 0, 0));
        return now < cutoff ? AttendanceMode.Masuk : AttendanceMode.Pulang;
    }

    public static bool TryParseJam(string text, out TimeSpan value) =>
        TimeSpan.TryParseExact(text.Trim(), @"hh\:mm", null, out value)
        || TimeSpan.TryParseExact(text.Trim(), @"h\:mm", null, out value);

    private static TimeSpan ParseOrDefault(string text, TimeSpan fallback) =>
        TryParseJam(text, out var value) ? value : fallback;
}
