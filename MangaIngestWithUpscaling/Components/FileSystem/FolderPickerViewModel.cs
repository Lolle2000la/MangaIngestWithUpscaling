﻿using MudBlazor;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;

namespace MangaIngestWithUpscaling.Components.FileSystem;

public class FolderPickerViewModel : ViewModelBase
{
    private readonly ObservableAsPropertyHelper<bool> _canGoToParent;
    private bool _disabled;
    private string? _errorMessage;
    private bool _loading;
    private string _rootDirectory = Directory.GetCurrentDirectory();
    private string? _selectedPath;
    private string _title = "Select Folder";

    public FolderPickerViewModel()
    {
        LoadDirectoryItemsCommand = ReactiveCommand.CreateFromTask(LoadDirectoryItemsAsync);
        GoToParentCommand = ReactiveCommand.Create(GoToParent);

        this.WhenAnyValue(x => x.RootDirectory)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .Select(_ => Unit.Default)
            .InvokeCommand(LoadDirectoryItemsCommand);

        _canGoToParent = this.WhenAnyValue(x => x.RootDirectory)
            .Select(path => !string.IsNullOrWhiteSpace(path) && Directory.GetParent(path) != null)
            .ToProperty(this, x => x.CanGoToParent);

        WhenPathSelected = this.WhenAnyValue(x => x.SelectedPath);
    }

    public string RootDirectory
    {
        get => _rootDirectory;
        set => this.RaiseAndSetIfChanged(ref _rootDirectory, value);
    }

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public bool Loading
    {
        get => _loading;
        set => this.RaiseAndSetIfChanged(ref _loading, value);
    }

    public string? SelectedPath
    {
        get => _selectedPath;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPath, value);
        }
    }

    public bool Disabled
    {
        get => _disabled;
        set => this.RaiseAndSetIfChanged(ref _disabled, value);
    }

    public bool CanGoToParent => _canGoToParent.Value;
    public ObservableCollection<TreeItemData<DirectoryItem>> TreeItems { get; } = [];
    public ReactiveCommand<Unit, Unit> LoadDirectoryItemsCommand { get; }
    public ReactiveCommand<Unit, Unit> GoToParentCommand { get; }
    public IObservable<string?> WhenPathSelected { get; }

    private async Task LoadDirectoryItemsAsync()
    {
        try
        {
            Loading = true;
            ErrorMessage = null;

            var directories = await Task.Run(() => Directory.GetDirectories(RootDirectory));
            var items = directories.Select(CreateDirectoryItem).ToList();

            TreeItems.Clear();
            foreach (var item in items)
            {
                TreeItems.Add(CreateTreeItemData(item));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error accessing directory: {ex.Message}";
        }
        finally
        {
            Loading = false;
        }
    }

    private void GoToParent()
    {
        var parent = Directory.GetParent(RootDirectory)?.FullName;
        if (parent != null) RootDirectory = parent;
    }

    private DirectoryItem CreateDirectoryItem(string path) => new()
    {
        Path = path, Name = Path.GetFileName(path), HasChildren = DirectoryHasSubdirectories(path)
    };

    private TreeItemData<DirectoryItem> CreateTreeItemData(DirectoryItem item) => new()
    {
        Value = item, Text = item.Name, Expandable = item.HasChildren, Selected = item.Path == SelectedPath
    };

    public async Task<IReadOnlyCollection<TreeItemData<DirectoryItem>>> HandleExpand(DirectoryItem item)
    {
        try
        {
            item.ChildrenLoading = true;
            var subDirs = await Task.Run(() => Directory.GetDirectories(item.Path));

            item.Children = subDirs.Select(CreateDirectoryItem).ToList();
            return item.Children.Select(CreateTreeItemData).ToList();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error loading directory: {ex.Message}";
            return [];
        }
        finally
        {
            item.ChildrenLoading = false;
        }
    }

    public void HandleItemsLoaded(TreeItemData<DirectoryItem> parent,
        IReadOnlyCollection<TreeItemData<DirectoryItem?>>? children)
    {
        parent.Children = children?
            .OfType<TreeItemData<DirectoryItem>>() // remove nullable annotation
            .ToList();
    }

    private bool DirectoryHasSubdirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path).Length != 0;
        }
        catch
        {
            return false;
        }
    }
}