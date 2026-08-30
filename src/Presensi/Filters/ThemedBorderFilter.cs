using OpenCvSharp;

namespace Presensi.Filters;

/// <summary>
/// CONTOH filter tema (17 Agustus / Ramadan / dst) - border warna + teks
/// sederhana, sengaja MINIMAL supaya jelas ini cuma bukti arsitektur
/// "filter = lapisan tampilan doang", BUKAN hasil akhir. Ganti/tambah
/// dengan aset grafis asli (PNG overlay, stiker, dsb) kapan pun - cukup
/// buat class baru yang implement IPreviewFilter, tidak menyentuh kode
/// kamera/pengenalan wajah sama sekali.
/// </summary>
public sealed class ThemedBorderFilter : IPreviewFilter
{
    private readonly Scalar _color;
    private readonly string _label;

    public string DisplayName { get; }

    public static ThemedBorderFilter Kemerdekaan() =>
        new("17 Agustus - Merdeka!", new Scalar(0, 0, 220), "Tema Kemerdekaan");

    public static ThemedBorderFilter Ramadan() =>
        new("Marhaban ya Ramadan", new Scalar(0, 140, 40), "Tema Ramadan");

    private ThemedBorderFilter(string label, Scalar color, string displayName)
    {
        _label = label;
        _color = color;
        DisplayName = displayName;
    }

    public void Apply(Mat previewFrame)
    {
        const int thickness = 14;
        Cv2.Rectangle(previewFrame, new Rect(0, 0, previewFrame.Width, previewFrame.Height), _color, thickness);
        Cv2.PutText(previewFrame, _label, new Point(24, previewFrame.Height - 24),
            HersheyFonts.HersheySimplex, 0.8, Scalar.White, 2, LineTypes.AntiAlias);
    }
}
