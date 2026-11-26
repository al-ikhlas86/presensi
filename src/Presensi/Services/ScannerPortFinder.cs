using System.IO.Ports;
using Microsoft.Win32;
using Presensi.Logging;

namespace Presensi.Services;

/// <summary>
/// Mencari port COM scanner berdasarkan identitas perangkat USB-nya (VID/PID), bukan nomor COM. Windows bisa memberi nomor COM berbeda bila kabel
/// dicolok ke lubang USB lain atau PC memuat ulang driver; kalau yang diingat hanya "COM3", scanner yang sama terlihat hilang dan scan tidak masuk.
/// Identitas dibaca dari registri (HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_xxxx&amp;PID_xxxx\*\Device Parameters\PortName), tanpa paket tambahan.
/// </summary>
public static class ScannerPortFinder
{
    public sealed record PortInfo(string PortName, string? VidPid);

    /// <summary>Port yang sekarang ada di PC, lengkap dengan VID/PID bila port itu perangkat USB (null untuk COM bawaan/tak dikenal).</summary>
    public static IReadOnlyList<PortInfo> Daftar()
    {
        string[] ada;
        try { ada = SerialPort.GetPortNames(); }
        catch (Exception ex) { Log.Warn($"[Scanner] gagal membaca daftar port: {ex.Message}"); return Array.Empty<PortInfo>(); }
        var peta = PetaVidPid();
        return ada.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PortInfo(p, peta.TryGetValue(p, out var v) ? v : null)).ToList();
    }

    /// <summary>
    /// Port yang dipakai untuk menyambung ulang: (1) port dengan VID/PID yang sama dengan scanner yang pernah dipasangkan, di nomor COM mana pun;
    /// (2) bila tidak ada identitas tersimpan, nomor COM tersimpan selama masih ada. Null bila scanner tidak terlihat sama sekali.
    /// </summary>
    public static string? CariPort(string? idTersimpan, string? portTersimpan)
    {
        var daftar = Daftar();
        if (!string.IsNullOrWhiteSpace(idTersimpan))
        {
            var cocok = daftar.FirstOrDefault(p => string.Equals(p.VidPid, idTersimpan, StringComparison.OrdinalIgnoreCase));
            if (cocok != null) return cocok.PortName;
        }
        if (string.IsNullOrWhiteSpace(portTersimpan)) return null;
        var tersimpan = daftar.FirstOrDefault(p => string.Equals(p.PortName, portTersimpan, StringComparison.OrdinalIgnoreCase));
        if (tersimpan == null) return null;
        // Identitas tersimpan tetapi nomor COM lama kini milik perangkat USB lain: jangan disambungi.
        if (!string.IsNullOrWhiteSpace(idTersimpan) && tersimpan.VidPid != null) return null;
        return tersimpan.PortName;
    }

    private static Dictionary<string, string> PetaVidPid()
    {
        var hasil = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ganda = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return hasil;
            foreach (var vidPid in usb.GetSubKeyNames())
            {
                using var perangkat = usb.OpenSubKey(vidPid);
                if (perangkat == null) continue;
                foreach (var instans in perangkat.GetSubKeyNames())
                {
                    using var param = perangkat.OpenSubKey(instans + @"\Device Parameters");
                    var nama = param?.GetValue("PortName") as string;
                    if (string.IsNullOrWhiteSpace(nama)) continue;
                    // Entri lama perangkat yang sudah dicabut bisa masih menyimpan nama COM yang sama: kalau dua identitas berbeda mengklaim satu port, anggap tak dikenal.
                    if (hasil.TryGetValue(nama!, out var sudah) && !string.Equals(sudah, vidPid, StringComparison.OrdinalIgnoreCase)) ganda.Add(nama!);
                    else hasil[nama!] = vidPid;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Scanner] gagal membaca identitas perangkat dari registri: {ex.Message}");
        }
        foreach (var g in ganda) hasil.Remove(g);
        return hasil;
    }
}
