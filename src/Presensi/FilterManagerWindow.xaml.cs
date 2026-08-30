using System.Windows;
using Microsoft.Win32;
using Presensi.Filters;
using Window = System.Windows.Window;

namespace Presensi;

public partial class FilterManagerWindow : Window
{
    public bool FiltersChanged { get; private set; }

    private List<IPreviewFilter> _current = new();

    public FilterManagerWindow()
    {
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        DisposeCurrent();
        _current = FilterManager.LoadAll();
        FilterListBox.ItemsSource = _current;
    }

    private void DisposeCurrent()
    {
        foreach (var f in _current)
        {
            if (f is IDisposable d) d.Dispose();
        }
    }

    private void FilterListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        DeleteButton.IsEnabled = FilterListBox.SelectedItem is IPreviewFilter f && f.IsDeletable;
    }

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Gambar PNG (*.png)|*.png",
            Title = "Pilih gambar filter (PNG, idealnya latar transparan)",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            FilterManager.Import(dialog.FileName);
            FiltersChanged = true;
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Gagal upload filter", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (FilterListBox.SelectedItem is not IPreviewFilter filter) return;

        var confirm = MessageBox.Show(this, $"Hapus filter \"{filter.DisplayName}\"? Tidak bisa dibatalkan.",
            "Konfirmasi Hapus", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            FilterManager.Delete(filter);
            FiltersChanged = true;
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Gagal hapus filter", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) => DisposeCurrent();
}
