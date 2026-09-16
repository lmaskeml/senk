using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AndroidManager.Security.ViewModels;

public sealed partial class BankHideCheckItemViewModel : ObservableObject
{
    public BankHideCheckItemViewModel(BankHideCheckItem item)
    {
        Id = item.Id;
        Title = item.Title;
        Detail = item.Detail;
        FixHint = item.FixHint;
        Status = item.Status;
    }

    public string Id { get; }
    public string Title { get; }
    public string Detail { get; }
    public string FixHint { get; }
    public BankHideCheckStatus Status { get; }

    public string StatusLabel => Status switch
    {
        BankHideCheckStatus.Pass => "OK",
        BankHideCheckStatus.Fail => "EKSIK",
        BankHideCheckStatus.Warn => "UYARI",
        _ => "?"
    };
}
