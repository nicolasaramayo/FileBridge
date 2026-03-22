using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileBridge.Application.Services;
using FileBridge.Domain.Interfaces;
using FileBridge.UI.Services;
using System.Collections.ObjectModel;

namespace FileBridge.UI.ViewModels;

public partial class TransferViewModel : ViewModelBase
{
    private readonly TransferOrchestrator _transferOrchestrator;
    private readonly ITransferStore _transferStore;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private ObservableCollection<Domain.Entities.TransferJob> _activeTransfers = new();

    [ObservableProperty]
    private ObservableCollection<Domain.Entities.TransferJob> _transferHistory = new();

    [ObservableProperty]
    private Domain.Entities.TransferJob? _selectedTransfer;

    public TransferViewModel(
        TransferOrchestrator transferOrchestrator,
        ITransferStore transferStore,
        INavigationService navigationService)
    {
        _transferOrchestrator = transferOrchestrator;
        _transferStore = transferStore;
        _navigationService = navigationService;
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        try
        {
            var history = await _transferStore.GetHistoryAsync();
            TransferHistory = new ObservableCollection<Domain.Entities.TransferJob>(history);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    [RelayCommand]
    private async Task PauseTransfer(Domain.Entities.TransferJob job)
    {
        if (job == null) return;

        try
        {
            await _transferOrchestrator.PauseAsync(job.Id.ToString());
            job.Status = Domain.Enums.TransferStatus.Paused;
        }
        catch (Exception ex)
        {
            SetError($"Failed to pause: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ResumeTransfer(Domain.Entities.TransferJob job)
    {
        if (job == null) return;

        try
        {
            await _transferOrchestrator.ResumeAsync(job.Id.ToString());
        }
        catch (Exception ex)
        {
            SetError($"Failed to resume: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CancelTransfer(Domain.Entities.TransferJob job)
    {
        if (job == null) return;

        try
        {
            await _transferOrchestrator.CancelAsync(job.Id.ToString());
            job.Status = Domain.Enums.TransferStatus.Cancelled;
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            SetError($"Failed to cancel: {ex.Message}");
        }
    }
}
