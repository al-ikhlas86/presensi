using OpenCvSharp;

namespace Presensi.Filters;

public sealed class NoFilter : IPreviewFilter
{
    public string DisplayName => "Tanpa Filter";
    public void Apply(Mat previewFrame) { /* sengaja kosong */ }
}
