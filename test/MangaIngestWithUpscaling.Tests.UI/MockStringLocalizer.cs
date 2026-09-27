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
        { "Status_Remote", "Remote" },
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
        {
            "Dialog_DeleteUpscaledChapter_Content",
            "Are you sure you want to delete the upscaled version of this chapter?"
        },
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
        // Mangas
        { "MergeSelected", "Merge Selected" },
        { "MoveSelected", "Move Selected" },
        { "DeleteSelected", "Delete Selected" },
        { "Tooltip_Edit", "Edit this manga" },
        { "Tooltip_Delete", "Delete this manga" },
        { "LibraryToBrowse", "Library to browse" },
        { "Library_All", "All" },
        { "SearchLabel", "Search" },
        { "SearchPlaceholder", "Search for mangas..." },
        { "Header_Library", "Library" },
        { "Header_Title", "Title" },
        { "Header_Chapters", "Chapters" },
        { "Header_ShouldUpscale", "Should Upscale" },
        { "ChapterCount", "{0} ({1} upscaled)" },
        { "Word_Chapter", "chapter" },
        { "UseLibrarySettings", "Use Library Settings" },
        // MergeMangaDialog
        { "Title_Multiple", "Merge {0} mangas" },
        { "Title_Single", "Merge {0}" },
        { "SelectPrimary", "Primary" },
        { "Button_Merge", "Merge" },
        { "Button_Cancel", "Cancel" },
        // MoveMangasToLibraryDialog
        { "SelectLibrary", "Library" },
        { "Button_Move", "Move" },
        // DeleteMangasDialog
        { "Button_Delete", "Delete" },
        { "Option_Original", "Delete original files" },
        { "Option_Upscaled", "Delete upscaled files" },
        // EditManga
        { "Header_EditManga", "Edit Manga" },
        { "Label_PrimaryTitle", "Primary title" },
        { "Label_AddOldTitle", "Add old title to alternative titles" },
        { "Label_UpscalerProfile", "Upscaler profile" },
        { "Label_ChapterMerging", "Chapter merging" },
        { "Button_SaveChanges", "Save Changes" },
        { "Button_AddTitle", "Add Title" },
        { "Label_AltTitle", "Alternative title" },
        { "Tooltip_RemoveTitle", "Remove title" },
        { "Success_Update", "Changes saved" },
        { "Error_FormValidation", "The form contains invalid values" },
        { "Error_ChangeTitle", "Failed to change the title" },
        { "Loading", "Loading" },
        // Libraries (page)
        { "CreateLibrary", "Create Library" },
        { "UpscaleAll", "Upscale All" },
        { "NoLibrariesYet", "No Libraries Yet" },
        { "CreateFirstLibraryDescription", "Create your first library to start ingesting manga." },
        { "CreateYourFirstLibrary", "Create your first library" },
        { "LibraryOverview", "Library Overview" },
        { "Stats_Libraries", "{0}" },
        { "Stats_MangaSeries", "{0}" },
        { "Stats_Chapters", "{0}" },
        { "Word_Library", "library" },
        { "Word_MangaSeries", "manga series" },
        { "Header_LibraryName", "Name" },
        { "Header_IngestPath", "Ingest Path" },
        { "Header_UpscalerConfig", "Upscaler" },
        { "NotConfigured", "Not configured" },
        { "AutoUpscale", "Auto upscale" },
        { "Tooltip_Scan", "Scan" },
        { "Tooltip_Integrity", "Check integrity" },
        { "Tooltip_Filters", "Image filters" },
        { "DeleteDialog_Title", "Delete Library" },
        { "DeleteDialog_Content", "Are you sure you want to delete this library?" },
        // EditLibrary
        { "SaveButton", "Save Changes" },
        // AddImageFilterDialog / EditImageFilterDialog
        { "Save", "Save" },
        { "Close", "Close" },
        { "Button_AddFilter", "Add Filter" },
        { "Button_SelectImage", "Select Image File" },
        { "Label_Description", "Description" },
        { "Placeholder_Description", "Why is this image being filtered?" },
        { "Text_NoDescription", "No description" },
        { "Text_Never", "Never" },
        { "Word_FilteredImage", "image" },
        { "Text_NoFilteredImages", "No filtered images" },
        { "Header_Preview", "Preview" },
        { "Header_Filename", "Filename" },
        { "Header_Description", "Description" },
        { "Header_Added", "Added" },
        { "Header_Occurrences", "Occurrences" },
        { "Header_LastMatched", "Last Matched" },
        { "Button_ApplyRetroactively", "Apply retroactively" },
        { "NoRecords_Title", "No records" },
        { "NoRecords_Subtitle", "Nothing matches your search" },
        // PreviewLibraryRenames
        { "Panel_ExistingSeries", "Existing Series" },
        { "Header_OriginalSeries", "Original" },
        { "Header_RenamedSeries", "Renamed" },
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
            if (typeof(T).Name == "Libraries" && name == "Title")
            {
                return new LocalizedString(name, "Libraries");
            }
            if (typeof(T).Name == "FilteredImages" && name == "Title")
            {
                return new LocalizedString(name, "Filtered Images");
            }
            if (typeof(T).Name == "AddImageFilterDialog" && name == "Title")
            {
                return new LocalizedString(name, "Add Image Filter");
            }
            if (typeof(T).Name == "EditImageFilterDialog" && name == "Title")
            {
                return new LocalizedString(name, "Edit Image Filter");
            }
            if (typeof(T).Name == "PreviewLibraryRenames" && name == "Title")
            {
                return new LocalizedString(name, "Rename Preview");
            }
            if (typeof(T).Name == "LibraryRenameDialog" && name == "Title")
            {
                return new LocalizedString(name, "Rename Rules");
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
