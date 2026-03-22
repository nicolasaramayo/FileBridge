using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileBridge.Application.Services;
using FileBridge.Domain.Entities;
using FileBridge.UI.Services;
using System.Collections.ObjectModel;
using DomainDevice = FileBridge.Domain.Entities.Device;

namespace FileBridge.UI.ViewModels;

public partial class FileBrowserViewModel : ViewModelBase
{
    private readonly FileService _fileService;
    private readonly INavigationService _navigationService;
    private DomainDevice? _currentDevice;

    [ObservableProperty]
    private ObservableCollection<FileItem> _files = new();

    [ObservableProperty]
    private string _currentDirectory = "/";

    [ObservableProperty]
    private FileItem? _selectedFile;

    [ObservableProperty]
    private ObservableCollection<FileItem> _selectedFiles = new();

    [ObservableProperty]
    private bool _isMultiSelectMode;

    public FileBrowserViewModel(FileService fileService, INavigationService navigationService)
    {
        _fileService = fileService;
        _navigationService = navigationService;
    }

    public void SetDevice(DomainDevice device)
    {
        _currentDevice = device;
        // Start browsing from device's root or default folders
        CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        LoadFiles();
    }

    [RelayCommand]
    private async Task LoadFilesAsync()
    {
        IsBusy = true;
        ClearError();

        try
        {
            var files = await _fileService.GetFilesAsync(CurrentDirectory);
            Files.Clear();
            foreach (var file in files)
            {
                Files.Add(file);
            }
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadFiles()
    {
        _ = LoadFilesAsync();
    }

    [RelayCommand]
    private async Task NavigateToFolderAsync(FileItem? item)
    {
        if (item == null || !IsDirectory(item)) return;

        CurrentDirectory = item.FilePath;
        await LoadFilesAsync();
    }

    [RelayCommand]
    private async Task NavigateUpAsync()
    {
        var parent = Path.GetDirectoryName(CurrentDirectory);
        if (!string.IsNullOrEmpty(parent))
        {
            CurrentDirectory = parent;
            await LoadFilesAsync();
        }
    }

    [RelayCommand]
    private async Task SelectFileAsync(FileItem? file)
    {
        if (file == null) return;

        if (IsMultiSelectMode)
        {
            if (SelectedFiles.Contains(file))
                SelectedFiles.Remove(file);
            else
                SelectedFiles.Add(file);
            return;
        }

        // Navigate to transfer page for single file selection
        if (_currentDevice != null)
        {
            await _navigationService.GoToAsync("//transfers", new Dictionary<string, object>
            {
                { "file", file },
                { "device", _currentDevice }
            });
        }
    }

    [RelayCommand]
    private async Task PickFileAsync()
    {
        try
        {
            var result = await FilePicker.PickAsync();
            if (result != null)
            {
                // Get file size using FileInfo
                long fileSize = 0;
                if (File.Exists(result.FullPath))
                {
                    fileSize = new FileInfo(result.FullPath).Length;
                }

                var fileItem = new FileItem
                {
                    Id = Guid.NewGuid(),
                    FileName = result.FileName,
                    FilePath = result.FullPath,
                    FileSize = fileSize,
                    Direction = Domain.Enums.TransferDirection.Send,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                if (_currentDevice != null)
                {
                    await _navigationService.GoToAsync("//transfers", new Dictionary<string, object>
                    {
                        { "file", fileItem },
                        { "device", _currentDevice }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to pick file: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleMultiSelect()
    {
        IsMultiSelectMode = !IsMultiSelectMode;
        if (!IsMultiSelectMode)
        {
            SelectedFiles.Clear();
        }
    }

    [RelayCommand]
    private async Task SendSelectedAsync()
    {
        if (_currentDevice == null || SelectedFiles.Count == 0) return;

        await _navigationService.GoToAsync("//transfers", new Dictionary<string, object>
        {
            { "files", SelectedFiles.ToList() },
            { "device", _currentDevice }
        });
    }

    private static bool IsDirectory(FileItem item)
    {
        return Directory.Exists(item.FilePath);
    }
}
