using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace NovaDownloader.UI.Dialogs;

public partial class AddDownloadDialog : Window
{
    public string DownloadUrl => UrlTextBox.Text.Trim();
    public string DestinationPath => PathTextBox.Text.Trim();
    public int SegmentCount => (int)SegmentsSlider.Value;

    public AddDownloadDialog(string? initialUrl = null)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(initialUrl) && (initialUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || initialUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            UrlTextBox.Text = initialUrl;
            UpdateDestinationPath(initialUrl);
        }
    }

    private void UrlTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var url = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(PathTextBox.Text) && !string.IsNullOrWhiteSpace(url))
        {
            UpdateDestinationPath(url);
        }
    }

    private void UpdateDestinationPath(string url)
    {
        try
        {
            var uri = new Uri(url);
            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
            {
                fileName = "download_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            }

            var downloadsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            PathTextBox.Text = Path.Combine(downloadsFolder, fileName);
        }
        catch
        {
            // invalid URL format, ignore
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var currentPath = PathTextBox.Text.Trim();
        var initialDir = Path.GetDirectoryName(currentPath);
        var initialFile = Path.GetFileName(currentPath);

        var dlg = new SaveFileDialog
        {
            InitialDirectory = Directory.Exists(initialDir) ? initialDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            FileName = initialFile
        };

        if (dlg.ShowDialog() == true)
        {
            PathTextBox.Text = dlg.FileName;
        }
    }

    private void SegmentsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SegmentsLabel != null)
        {
            SegmentsLabel.Text = $"{(int)SegmentsSlider.Value} Threads";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DownloadUrl))
        {
            MessageBox.Show("Please enter a valid download URL.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            MessageBox.Show("Please specify a destination file path.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}
