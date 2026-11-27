using System;

namespace Presensi.Services;

/// <summary>
/// Penilaian posisi wajah terhadap lingkaran pendaftaran (port dari webview src/utils/faceGeometry.ts: jaga keduanya sejajar).
/// Server mengirim pusat dan lebar wajah sebagai pecahan dari FRAME; di sini diubah ke koordinat kotak tampilan (Image Stretch=Uniform = letterbox)
/// lalu dibandingkan dengan lingkaran yang digambar. Pose 3D InsightFace dipakai untuk hadap/dongak/miring (yaw lama berubah puluhan derajat hanya karena kepala miring).
/// Ambang klien sedikit lebih ketat dari server (POSE_*_MAKS 20/25/30).
/// </summary>
public static class GeometriWajah
{
    public const double LingkarCy = 0.46;
    public const double LingkarR = 0.36;

    private const double PoseYawMaks = 16;
    private const double PosePitchMaks = 20;
    private const double PoseRollMaks = 25;
    private const double YawLamaMaks = 12;
    private const double SimpanganMaks = 0.35;
    private const double LebarMin = 0.55;
    private const double LebarMaks = 0.95;
    private const double BlurMin = 30;
    private const double TerangMin = 45;

    /// <summary>Satu petunjuk terpenting, atau null bila posisi pas. Geser horizontal sengaja netral (arah cermin kamera kiosk tidak diuji).</summary>
    public static string? Nilai(AnalisisWajah a, double frameW, double frameH, double kotakW, double kotakH)
    {
        var r = Math.Max(10, Math.Min(kotakW, kotakH) * LingkarR);
        var cxL = kotakW / 2;
        var cyL = kotakH * LingkarCy;

        if (a.Cx is { } cx && a.Cy is { } cy && a.Lebar is { } w && frameW > 0 && frameH > 0 && kotakW > 0 && kotakH > 0)
        {
            var skala = Math.Min(kotakW / frameW, kotakH / frameH); // Uniform: seluruh frame terlihat (tidak dipotong)
            var x = kotakW / 2 + (cx - 0.5) * frameW * skala;
            var y = kotakH / 2 + (cy - 0.5) * frameH * skala;
            var rasio = w * frameW * skala / (r * 2);
            if (rasio < LebarMin) return "Dekatkan wajah";
            if (rasio > LebarMaks) return "Mundur sedikit";
            var dx = x - cxL;
            var dy = y - cyL;
            if (Math.Sqrt(dx * dx + dy * dy) > SimpanganMaks * r)
                return Math.Abs(dy) >= Math.Abs(dx) ? (dy > 0 ? "Naikkan wajah sedikit" : "Turunkan wajah sedikit") : "Pusatkan wajah di dalam lingkaran";
        }
        else if (a.UkuranWajah > 0 && a.UkuranWajah < 120) return "Dekatkan wajah";

        if (a.PoseYaw is { } py && a.PosePitch is { } pp)
        {
            if (Math.Abs(py) > PoseYawMaks) return "Hadap lurus ke kamera";
            if (Math.Abs(pp) > PosePitchMaks) return "Jangan mendongak atau menunduk";
            if (a.PoseRoll is { } pr && Math.Abs(pr) > PoseRollMaks) return "Tegakkan kepala";
        }
        else if (Math.Abs(a.Yaw) > YawLamaMaks) return "Hadap lurus ke kamera";

        if (a.Brightness is { } br && br < TerangMin) return "Cari tempat yang lebih terang";
        if (a.Blur is { } bl && bl < BlurMin) return "Diam sebentar";
        return null;
    }
}

/// <summary>
/// Penahan posisi yang tidak mudah buyar: waktu "pas" bertambah selagi pas dan hanya BERKURANG pelan saat satu pembacaan meleset (bukan langsung nol).
/// Siap bila pas cukup lama DAN sekurangnya dua pembacaan baik.
/// </summary>
public sealed class PenahanPosisi
{
    private readonly double _tahanMs;
    private readonly int _pembacaanMin;
    private readonly double _peluruhanMs;
    private double _msPas;
    private int _baik;
    private DateTime _terakhir = DateTime.MinValue;

    public PenahanPosisi(double tahanMs, int pembacaanMin = 2, double peluruhanMs = 450)
    {
        _tahanMs = tahanMs;
        _pembacaanMin = pembacaanMin;
        _peluruhanMs = peluruhanMs;
    }

    public void Reset() { _msPas = 0; _baik = 0; _terakhir = DateTime.MinValue; }

    /// <summary>Catat satu pembacaan; mengembalikan isi cincin (0..1) dan apakah foto boleh diambil.</summary>
    public (double Isi, bool Siap) Catat(bool pas, DateTime sekarang)
    {
        var dt = _terakhir == DateTime.MinValue ? 0 : Math.Min((sekarang - _terakhir).TotalMilliseconds, 1200);
        _terakhir = sekarang;
        if (pas) { _msPas += Math.Max(dt, 200); _baik++; }
        else { _msPas = Math.Max(0, _msPas - _peluruhanMs); _baik = Math.Max(0, _baik - 1); }
        return (Math.Min(1, _msPas / _tahanMs), _msPas >= _tahanMs && _baik >= _pembacaanMin);
    }
}
