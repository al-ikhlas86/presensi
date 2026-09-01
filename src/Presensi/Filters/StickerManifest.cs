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

    /// <summary>
    /// Titik jangkar dasar SEBELUM AnchorYRatio/OffsetXRatio diterapkan
    /// sbg pergeseran halus - "box" (bawaan, dari kotak wajah kasar, dipakai
    /// net48 & selama model landmark belum siap) | "eyes" (titik tengah
    /// kedua mata - presisi utk kacamata) | "eyebrows" (titik tengah alis -
    /// presisi utk topi/helm/telinga yang duduk di atas kepala). Diabaikan
    /// (fallback ke "box") kalau FaceTracker.GetFaceLandmarks() null - lihat
    /// FaceStickerFilter.Apply(). 2026-09-01, diminta user demi presisi
    /// setara filter Instagram/TikTok.
    /// </summary>
    public string AnchorLandmark { get; set; } = "box";
}
