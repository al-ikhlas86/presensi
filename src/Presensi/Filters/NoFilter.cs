using OpenCvSharp;

namespace Presensi.Filters;

public sealed class NoFilter : IPreviewFilter
{
    public string DisplayName => "Normal";
    public bool IsDeletable => false;
    public string? FilePath => null;
    public void Apply(Mat previewFrame) { /* sengaja kosong */ }
}
