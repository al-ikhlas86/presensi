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
    void Apply(Mat previewFrame);
}
