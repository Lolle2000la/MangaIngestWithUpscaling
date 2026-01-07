using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Tests.UI;

public class MockStringLocalizer<T> : IStringLocalizer<T>
{
    private static readonly Dictionary<string, string> _translations = new()
    {
        // SharedResource
        { "Generic_Merge", "Merge" },
        { "Generic_Cancel", "Cancel" },
        { "Generic_Save", "Save" },
        { "Generic_Create", "Create" },
        { "Generic_Refresh", "Refresh" },
        { "Generic_Move", "Move" },
        { "Generic_OK", "OK" },
        { "Yes", "Yes" },
        { "No", "No" },
        // TaskQueues
        { "PageTitle", "Tasks" },
        { "StandardTasks", "Standard Tasks" },
        { "UpscalingTasks", "Upscaling Tasks" },
        { "Error_ClearCompleted", "Failed to clear completed tasks: {0}" },
        { "Error_ClearFailed", "Failed to clear failed tasks: {0}" },
        // TaskTable
        { "ClearFailed", "Clear Failed" },
        { "ClearCompleted", "Clear Completed" },
        { "Header_Name", "Name" },
        { "Header_QueuedAt", "Queued At" },
        { "Header_FinishedAt", "Finished At" },
        { "Header_Status", "Status" },
        { "Progress_Working", "Working…" },
        { "Status_Pending", "Pending" },
        { "Status_Processing", "Processing" },
        { "Status_Completed", "Completed" },
        { "Status_Failed", "Failed" },
        { "Status_Canceled", "Canceled" },
        // EditLibraryFilters
        { "Title", "Edit Ingest Filters" },
        {
            "Description",
            "Define filters to apply to the files in the ingest folder. Files that do not match the filters will not be ingested."
        },
        { "Header_PatternToMatch", "Pattern to match" },
        { "Header_PatternType", "Pattern Type" },
        { "Header_TargetField", "Target Field" },
        { "Header_Action", "Action" },
        // EditLibraryRenames
        { "Header_Pattern", "Pattern" },
        { "Header_Replacement", "Replacement" },
        // ChapterList
        { "Button_MergeSelected", "Merge Selected" },
        { "Button_RevertSelectedMerged", "Revert Selected" },
        { "Button_DeleteSelected", "Delete Selected" },
        { "Button_UpscaleSelected", "Upscale Selected" },
        { "Button_DeleteUpscaledSelected", "Delete Upscaled Selected" },
        { "Tooltip_MergeChapter", "Merge this chapter" },
        { "Tooltip_Merge", "Merge this chapter" },
        { "Tooltip_RevertMerged", "Revert this merged chapter" },
        { "Tooltip_DeleteChapter", "Delete chapter" },
        { "Tooltip_Upscale", "Upscale" },
        { "Tooltip_DeleteUpscaled", "Delete upscaled" },
        { "Dialog_DeleteUpscaledChapter_Title", "Delete Upscaled Chapter" },
        { "Dialog_DeleteUpscaledChapter_Content", "Are you sure you want to delete the upscaled version of this chapter?" },
        { "Header_ChapterTitle", "Chapter Title" },
        { "Header_ChapterPath", "Path" },
        { "Header_Upscaled", "Upscaled" },
        { "Header_Splits", "Splits" },
        { "Header_UpscalerProfile", "Profile" },
        { "Header_Actions", "Actions" },
        { "Status_Yes", "Yes" },
        { "Status_No", "No" },
        { "Text_NotAvailable", "N/A" },
        { "Chip_Merged", "Merged" },
        { "Chip_Pending", "Pending" },
        { "Chip_Detected", "Detected" },
        { "Chip_NoSplits", "No Splits" },
        { "Chip_Applied", "Applied" },
        { "Chip_Failed", "Failed" },
        { "Menu_ViewEdit", "View/Edit" },
        { "Menu_ApplySplits", "Apply Splits" },
        { "Suffix_Old", "(Old)" },
    };

    public LocalizedString this[string name]
    {
        get
        {
            // Handle context-specific keys
            if (typeof(T).Name.Contains("TaskQueues"))
            {
                if (name == "Title")
                    return new LocalizedString(name, "Currently running tasks");
            }
            if (typeof(T).Name.Contains("EditLibraryRenames"))
            {
                if (name == "Title")
                    return new LocalizedString(name, "Edit Rename Rules");
                if (name == "Description")
                    return new LocalizedString(
                        name,
                        "Define regex or substring replacements to apply during ingest."
                    );
            }
            if (typeof(T).Name.Contains("EditLibraryFilters") && name == "Title")
            {
                return new LocalizedString(name, "Edit Ingest Filters");
            }

            if (_translations.TryGetValue(name, out var value))
            {
                return new LocalizedString(name, value);
            }
            // Fallback: return the key itself if not found, or a default format
            return new LocalizedString(name, name);
        }
    }

    public LocalizedString this[string name, params object[] arguments] =>
        new LocalizedString(name, string.Format(this[name].Value, arguments));

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        Enumerable.Empty<LocalizedString>();
}
