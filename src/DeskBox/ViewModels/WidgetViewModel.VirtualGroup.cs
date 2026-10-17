using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

/// <summary>
/// Virtual group mode: the widget references files by absolute path
/// (<see cref="WidgetConfig.FilePaths"/>) and never moves or copies them, so
/// project files (video edit projects, media) keep their physical location.
/// </summary>
public partial class WidgetViewModel
{
    private readonly Dictionary<string, FileSystemWatcher> _virtualGroupWatchers =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsVirtualGroup => Config.IsVirtualGroup;

    /// <summary>
    /// Switches virtual group mode, persists it and reloads the item list from
    /// the matching source (referenced paths or the managed folder).
    /// </summary>
    public async Task SetVirtualGroupAsync(bool enabled)
    {
        if (_isDisposed || Config.IsVirtualGroup == enabled)
        {
            return;
        }

        Config.IsVirtualGroup = enabled;
        _settingsService.UpdateWidget(Config);
        OnPropertyChanged(nameof(IsVirtualGroup));
        if (string.IsNullOrEmpty(CurrentFolderPath))
        {
            return;
        }

        await ConfigureFolderWatchersAsync(CurrentFolderPath);
        await ReloadFolderContentsAsync(CurrentFolderPath);
    }

    private static string NormalizeVirtualPath(string path) => Path.GetFullPath(path);

    /// <summary>
    /// Records the dropped paths in <see cref="WidgetConfig.FilePaths"/> and
    /// shows them. Nothing is moved; the caller receives an empty "moved"
    /// list so source widgets and the desktop are left untouched.
    /// </summary>
    private async Task<IReadOnlyList<string>> ImportVirtualPathsAsync(
        IReadOnlyList<string> paths,
        int? preferredManualIndex,
        CancellationToken cancellationToken)
    {
        var added = new List<string>();
        foreach (string path in paths)
        {
            string fullPath;
            try
            {
                fullPath = NormalizeVirtualPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                continue;
            }

            if (Config.FilePaths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            Config.FilePaths.Add(fullPath);
            added.Add(fullPath);
        }

        if (added.Count == 0)
        {
            return [];
        }

        _settingsService.UpdateWidget(Config);
        int? nextIndex = preferredManualIndex;
        using (EnterItemMutationScope())
        {
            foreach (string fullPath in added)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecordFileAddedAt(fullPath, DateTimeOffset.Now);
                if (await UpsertFolderItemAsync(fullPath, nextIndex) && nextIndex.HasValue)
                {
                    nextIndex++;
                }
            }
        }

        RefreshVirtualGroupWatchers();
        return [];
    }

    private async Task<bool> LoadVirtualGroupContentsAsync(
        CancellationToken cancellationToken,
        Action? beforeItemsReplaced)
    {
        var items = new List<WidgetItem>();
        foreach (string path in Config.FilePaths.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            WidgetItem? item = await _fileService.TryCreateWidgetItemAsync(
                path,
                hideShortcutArrowOverlay: _hideShortcutArrowOverlay,
                showImageFilesAsIcons: _showImageFilesAsIcons,
                showFileExtensions: _showFileExtensions,
                hideShortcutExtensionWhenShowingFileExtensions: _hideShortcutExtensionWhenShowingFileExtensions,
                loadIcon: false,
                loadFolderItemCount: false,
                loadShortcutTarget: false);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_itemMutationBatchDepth > 0)
        {
            _pendingFolderRefreshAfterBatch = true;
            MarkItemMutationBatchDirty();
            return false;
        }

        ApplyPersistedAddedTimes(items);
        beforeItemsReplaced?.Invoke();
        SyncFolderItems(items);
        SortItems();
        StartItemHydration();
        return true;
    }

    /// <summary>
    /// Watches the directories that contain referenced files so a rename or
    /// delete outside DeskBox is mirrored into <see cref="WidgetConfig.FilePaths"/>.
    /// </summary>
    private void RefreshVirtualGroupWatchers()
    {
        if (_isDisposed || !Config.IsVirtualGroup)
        {
            StopVirtualGroupWatchers();
            return;
        }

        HashSet<string> wanted = Config.FilePaths
            .Select(Path.GetDirectoryName)
            .Where(dir => !string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            .Select(dir => dir!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string stale in _virtualGroupWatchers.Keys.Where(dir => !wanted.Contains(dir)).ToArray())
        {
            _virtualGroupWatchers[stale].Dispose();
            _virtualGroupWatchers.Remove(stale);
        }

        foreach (string directory in wanted)
        {
            if (_virtualGroupWatchers.ContainsKey(directory))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = false
                };
                watcher.Renamed += (_, e) => PostVirtualChange(() => ApplyVirtualRename(e.OldFullPath, e.FullPath));
                watcher.Deleted += (_, e) => PostVirtualChange(() => ApplyVirtualDelete(e.FullPath));
                watcher.EnableRaisingEvents = true;
                _virtualGroupWatchers[directory] = watcher;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                App.Log($"[VirtualGroup] Watcher unavailable dir='{directory}' {ex.GetType().Name}");
            }
        }
    }

    private void StopVirtualGroupWatchers()
    {
        foreach (FileSystemWatcher watcher in _virtualGroupWatchers.Values)
        {
            watcher.Dispose();
        }

        _virtualGroupWatchers.Clear();
    }

    private void PostVirtualChange(Action apply)
    {
        // FileSystemWatcher raises on a thread-pool thread; the item list and
        // config belong to the UI thread.
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed && Config.IsVirtualGroup)
            {
                apply();
            }
        });
    }

    private void ApplyVirtualDelete(string path)
    {
        int index = Config.FilePaths.FindIndex(
            p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        Config.FilePaths.RemoveAt(index);
        RemoveItemByPath(path);
        _settingsService.UpdateWidget(Config, notifySubscribers: false);
        RefreshVirtualGroupWatchers();
    }

    private async void ApplyVirtualRename(string oldPath, string newPath)
    {
        try
        {
            int index = Config.FilePaths.FindIndex(
                p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            Config.FilePaths[index] = newPath;
            RemoveItemByPath(oldPath);
            await UpsertFolderItemAsync(newPath);
            _settingsService.UpdateWidget(Config, notifySubscribers: false);
            RefreshVirtualGroupWatchers();
        }
        catch (Exception ex)
        {
            App.Log($"[VirtualGroup] Rename sync failed {ex.GetType().Name}: {ex.Message}");
        }
    }
}
