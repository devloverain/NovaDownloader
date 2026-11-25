using System.IO;
using System.Windows;
using NovaDownloader.Core.Models;

namespace NovaDownloader.UI.Dialogs;

public enum DeleteChoice
{
    Cancel,
    DeleteFromListOnly,
    DeleteFileAndList
}

public partial class DeleteConfirmDialog : Window
{
    public DeleteChoice Choice { get; private set; } = DeleteChoice.Cancel;

    public DeleteConfirmDialog(IReadOnlyList<DownloadTask> tasks)
    {
        InitializeComponent();

        HeaderMessage.Text = tasks.Count == 1
            ? $"Remove \"{tasks[0].FileName}\"?"
            : $"Remove {tasks.Count} selected downloads?";

        const int maxPreview = 5;
        var previewNames = tasks.Take(maxPreview).Select(t => $"• {t.FileName}").ToList();
        if (tasks.Count > maxPreview)
        {
            previewNames.Add($"...and {tasks.Count - maxPreview} more");
        }
        FileNamesList.ItemsSource = previewNames;

        var missingCount = 0;
        foreach (var t in tasks)
        {
            if (!SafeFileExists(t.DestinationPath) && !SafeFileExists(t.IncompleteFilePath))
            {
                missingCount++;
            }
        }

        if (missingCount > 0)
        {
            MissingHintText.Visibility = Visibility.Visible;
            MissingHintText.Text = missingCount == tasks.Count
                ? "Note: File(s) were moved or are no longer on disk — removing will safely clean up the list."
                : $"Note: {missingCount} of {tasks.Count} file(s) were moved or missing on disk (remaining files will be deleted if you choose Delete File & List).";
        }
    }

    private static bool SafeFileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private void DeleteFromListButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = DeleteChoice.DeleteFromListOnly;
        DialogResult = true;
        Close();
    }

    private void DeleteFileAndListButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = DeleteChoice.DeleteFileAndList;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = DeleteChoice.Cancel;
        DialogResult = false;
        Close();
    }
}
