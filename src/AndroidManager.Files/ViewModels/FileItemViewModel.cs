using System.IO;
using AndroidManager.Core.Models;
using AndroidManager.Files.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AndroidManager.Files.ViewModels;

public sealed partial class FileItemViewModel : ObservableObject
{
    public FileSystemItem Model { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _editName = string.Empty;

    public string Icon => FileIconResolver.GetEmoji(Model.Name, Model.IsDirectory);
    public string Name => Model.Name;
    public string FullPath => Model.FullPath;
    public bool IsDirectory => Model.IsDirectory;
    public string Size => Model.SizeFormatted;
    public string Modified => Model.Modified == default
        ? string.Empty
        : Model.Modified.ToString("yyyy-MM-dd HH:mm");
    public string TypeLabel => Model.IsDirectory
        ? "Klasör"
        : string.IsNullOrEmpty(Model.Extension)
            ? "Dosya"
            : Model.Extension.TrimStart('.').ToUpperInvariant() + " Dosyası";

    public FileItemViewModel(FileSystemItem model)
    {
        Model = model;
        EditName = model.Name;
    }
}

public sealed record BreadcrumbItem(string Label, string Path);

public sealed class PcFileItemViewModel
{
    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public long Size { get; }
    public DateTime Modified { get; }

    public string Icon => FileIconResolver.GetEmoji(Name, IsDirectory);

    public string SizeFormatted => IsDirectory
        ? string.Empty
        : Size switch
        {
            < 1024 => $"{Size} B",
            < 1_048_576 => $"{Size / 1024.0:F1} KB",
            < 1_073_741_824 => $"{Size / 1_048_576.0:F1} MB",
            _ => $"{Size / 1_073_741_824.0:F2} GB"
        };

    public string ModifiedText => Modified == default
        ? string.Empty
        : Modified.ToString("yyyy-MM-dd HH:mm");

    public PcFileItemViewModel(string fullPath, bool isDirectory, long size, DateTime modified, string? name = null)
    {
        FullPath = fullPath;
        Name = name ?? Path.GetFileName(fullPath);
        IsDirectory = isDirectory;
        Size = size;
        Modified = modified;
    }
}
