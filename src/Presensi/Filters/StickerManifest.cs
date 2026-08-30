namespace Presensi.Filters;

/// <summary>
/// Isi manifest.json di dalam bundle .stiker - lihat FaceStickerFilter &amp;
/// README ("Bikin Filter Stiker Sendiri") utk penjelasan tiap angka.
/// </summary>
public sealed class StickerManifest
{
    public string DisplayName { get; set; } = "Stiker";

    /// <summary>Lebar stiker relatif thd lebar wajah terdeteksi (1.0 = sama lebar wajah).</summary>
    public double WidthRatio { get; set; } = 1.4;

    /// <summary>
    /// Posisi vertikal TITIK TENGAH stiker, dalam satuan "tinggi wajah" dari
    /// TITIK ATAS kotak wajah. 0 = pas di garis atas kepala, negatif = di
    /// atas kepala (mis. -0.4 utk topi/helm), positif kecil = turun ke area
    /// mata (mis. 0.25 utk kacamata).
    /// </summary>
    public double AnchorYRatio { get; set; } = -0.3;

    /// <summary>Geser horizontal, satuan "lebar wajah" (0 = tengah, sudah pas utk hampir semua stiker simetris).</summary>
    public double OffsetXRatio { get; set; } = 0.0;
}
