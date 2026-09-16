using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AndroidManager.Security.ViewModels;

public sealed partial class RomPartitionItem : ObservableObject
{
    public required string Name { get; init; }
    public RomBackupCategory Category { get; init; }
    public long SizeBytes { get; init; }
    public bool IsSlotAlias { get; init; }
    public string SizeFormatted { get; init; } = "";
    public string CategoryLabel { get; init; } = "";

    public bool CanSelect => !IsSlotAlias;

    [ObservableProperty] private bool _isSelected;
}
