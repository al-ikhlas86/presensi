using OpenCvSharp;

namespace Presensi.Filters;

/// <summary>
/// Filter dekoratif MURNI tampilan - lihat catatan penting di
/// MainWindow.xaml.cs soal kenapa ini TIDAK PERNAH boleh dipanggil di jalur
/// RawFrameCaptured (pipeline pengenalan wajah). Apply() memodifikasi frame
/// preview di tempat (in-place) - frame yang masuk ke sini SUDAH terpisah
/// total dari frame mentah yang dikirim ke server.
/// </summary>
public interface IPreviewFilter
{
    string DisplayName { get; }

    /// <summary>False HANYA utk filter "Normal" (default, tidak bisa dihapus user).</summary>
    bool IsDeletable { get; }

    /// <summary>Path file PNG di disk kalau filter ini berbasis gambar upload user, null kalau bawaan (mis. Normal).</summary>
    string? FilePath { get; }

    void Apply(Mat previewFrame);
}
