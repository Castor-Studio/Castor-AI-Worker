using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Castor.AI.Dashboard.ViewModels;

namespace Castor.AI.Dashboard.Views;

public sealed partial class MainWindow : Window
{
    private static readonly FilePickerFileType MediaFiles = new("Audio / video")
    {
        Patterns = ["*.wav", "*.mp3", "*.m4a", "*.aac", "*.flac", "*.ogg", "*.mp4", "*.mkv", "*.mov", "*.webm"],
    };

    private static readonly FilePickerFileType VideoFiles = new("Image / video")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.mp4", "*.mkv", "*.mov", "*.webm", "*.avi"],
    };

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.AudioFilePicker = () => PickFileAsync("Audio of this microphone", MediaFiles);
                viewModel.VideoFilePicker = () => PickFileAsync("Video of this camera", VideoFiles);
            }
        };
    }

    private async Task<string?> PickFileAsync(string title, FilePickerFileType type)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [type, FilePickerFileTypes.All],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
