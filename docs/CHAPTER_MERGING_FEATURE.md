# Chapter Part Merging Feature

## Overview
This feature allows automatic merging of chapter parts (e.g., Chapter 22.1, 22.2, 22.3) into single chapters (Chapter 22) during manga ingestion. The feature is configurable at both library and series levels and includes full revert capability.

## Configuration

### Library Level
- Navigate to Library settings in the web interface
- Check "Merge Chapter Parts" to enable merging for all series in the library
- This setting applies to all manga series unless overridden at the series level

### Series Level  
- Individual manga series can override the library setting
- Series-level setting takes precedence over library setting
- Allows fine-grained control per manga series

## How It Works

### Chapter Detection  
- Uses regex pattern to identify chapter parts: `(\d+(?:\.\d+)*)`
- Groups chapters with the same base number (e.g., 22.1, 22.2, 22.3 all belong to Chapter 22)
- **Supports flexible consecutive sequences**:
  - Can start with base number: `22 → 22.1 → 22.2`
  - Can start with .1: `22.1 → 22.2 → 22.3`
  - Special case base + .2: `22 → 22.2`
  - Special case can continue: `22 → 22.2 → 22.3 → 22.4`
- **Prevents merging of non-consecutive or special chapters**: Won't merge gaps like `22.1, 22.3` or special chapters like `5, 5.5`

### Merging Process
1. **Groups Detection**: Identifies chapter parts that share the same base number
2. **Sequence Validation**: Verifies that parts follow consecutive decimal steps
   - **Standard validation**: Chapters must increment by 0.1 starting from .1 (e.g., 22.1 → 22.2 → 22.3)
   - **Special case**: Base chapter + .2 only (e.g., 22 + 22.2) is allowed
   - **Rejects non-consecutive**: Won't merge 22.1 + 22.3 (missing 22.2)
   - **Rejects special chapters**: Won't merge 5 + 5.5 (5.5 is likely an extra/special chapter)
3. **Content Combination**: Merges all pages from validated part files into a single CBZ
4. **Natural Sorting**: Orders pages using natural string comparison for proper sequence
5. **Metadata Preservation**: Stores original chapter information for revert capability
6. **File Management**: Deletes original part files after successful merge

### Page Ordering
- Uses natural string sorting algorithm for correct page order within each part
- Handles both numeric (1, 2, 10) and alphanumeric (1a, 1b, 2) sequences  
- Pages are **concatenated** by part: all pages from Chapter 22.1, then all pages from Chapter 22.2, etc.
- Within each part, pages are sorted naturally by filename

## Database Schema

### New Tables
- **MergedChapterInfos**: Tracks merged chapters for revert capability
  - Links to original Chapter record
  - Stores JSON of original chapter parts (title, file path, page count)
  - Records merge timestamp

### New Columns
- **Libraries.MergeChapterParts**: Boolean flag for library-level setting
- **MangaSeries.MergeChapterParts**: Nullable boolean for series-level override

## Revert Capability

### Automatic Tracking
- All merge operations are automatically tracked in the database
- Original chapter metadata is preserved in JSON format
- Includes original titles, file paths, and page counts

### Revert Process
- The `ChapterPartMerger.RestoreChapterPartsAsync()` method can restore merged chapters
- Recreates original CBZ files from merged content
- Restores original database records
- Removes merge tracking information

## Important Notes

### Latest Chapter Exclusion
- **The latest chapter in a series is never merged, even if it has parts**
- This prevents merging incomplete ongoing chapters
- **Retroactive merging**: When new chapters are ingested, previously unmerged parts are checked and merged if they're no longer the latest
- Example: If Chapter 23.1 is the latest, it won't be merged until Chapter 24+ is ingested, at which point Chapter 23 parts will be merged automatically

### File Format Support
- Currently supports CBZ (Comic Book ZIP) format
- Maintains image quality and metadata during merge operations
- Preserves original file structure within archives

### Integration Points
- Integrated into the main ingestion pipeline (`IngestProcessor`)
- Works with existing upscaling workflow
- Compatible with all existing metadata handling

## Usage Examples

### Example 1: Standard Consecutive Parts Starting with .1
```
Before: Chapter 22.1.cbz, Chapter 22.2.cbz, Chapter 22.3.cbz
After:  Chapter 22.cbz (contains all pages from parts)
```

### Example 2: Sequence Starting with Base Number
```
Before: Chapter 22.cbz, Chapter 22.1.cbz, Chapter 22.2.cbz
After:  Chapter 22.cbz (merged, contains pages from all three)
```

### Example 3: Special Case (Base + .2)
```
Before: Chapter 22.cbz, Chapter 22.2.cbz
After:  Chapter 22.cbz (merged, contains pages from both)
```

### Example 4: Special Case Continuing as Regular Sequence
```
Before: Chapter 22.cbz, Chapter 22.2.cbz, Chapter 22.3.cbz, Chapter 22.4.cbz
After:  Chapter 22.cbz (merged, contains all pages in order)
```

### Example 5: Mixed Chapters (Some Merge, Some Don't)
```
Before: Chapter 21.cbz, Chapter 22.1.cbz, Chapter 22.2.cbz, Chapter 23.5.cbz, Chapter 24.cbz
After:  Chapter 21.cbz, Chapter 22.cbz, Chapter 23.5.cbz, Chapter 24.cbz
Result: Only 22.1 and 22.2 merged (consecutive), 23.5 remains separate (special chapter)
```

### Example 6: Non-Consecutive Parts (No Merge)
```
Before: Chapter 22.1.cbz, Chapter 22.3.cbz (missing 22.2)
After:  Chapter 22.1.cbz, Chapter 22.3.cbz (unchanged - not consecutive)
```

### Example 3: Latest Chapter Protection & Retroactive Merging
```
Initial state: Chapter 20.cbz, Chapter 21.1.cbz, Chapter 21.2.cbz
Result: No merging occurs (Chapter 21.2 is the latest)

After Chapter 22.1 is ingested:
Result: Chapter 21.1 and 21.2 are automatically merged into Chapter 21.cbz
Final state: Chapter 20.cbz, Chapter 21.cbz, Chapter 22.1.cbz

After Chapter 23.cbz is ingested:
Result: Chapter 22.1 remains unmerged (only 1 part), but now Chapter 22.1 could be merged if Chapter 22.2 appears later
```

## Technical Implementation

### EF Core ValueComparer
- The `OriginalParts` property uses a strongly-typed `List<OriginalChapterPart>` with JSON conversion
- EF Core ValueComparer is configured to properly detect changes in the collection
- Includes proper `Equals()` and `GetHashCode()` implementations for change tracking

### Key Classes
- **ChapterPartMerger**: Main service for merge operations with consecutive part validation
- **MergedChapterInfo**: Entity tracking merge operations  
- **ChapterMergeRevertService**: Handles revert operations
- **NaturalStringComparer**: Ensures proper page ordering

### Consecutive Part Validation
- **AreConsecutiveChapterParts()**: Validates chapter sequences with flexible rules
- **Flexible starting points**: Sequences can begin with base number (22) or first decimal part (22.1)
- **Base number rules**: If present, base number must be first and can be followed by .1 or .2
- **Consecutive requirements**: After the starting point, all parts must increment by exactly 0.1
- **Special case handling**: `22 → 22.2` is valid and can continue as `22 → 22.2 → 22.3 → 22.4`
- **Gap detection**: Rejects sequences with missing parts (e.g., `22.1, 22.3` missing `22.2`)
- **Floating-point tolerance**: Uses 0.001m tolerance for decimal comparison accuracy
- **Special chapter protection**: Prevents merging non-standard decimals (e.g., `5, 5.5` where `5.5` is an extra chapter)

### Service Registration
```csharp
builder.Services.RegisterScoped<IChapterPartMerger, ChapterPartMerger>();
```

### Configuration Integration
- Library settings UI updated with merge checkbox
- Database migrations applied for new schema
- Full integration with existing ingestion pipeline

## Migration Information
- Migration: `20250724195008_AddChapterMerging`
- Database: SQLite with JSON column support
- Compatibility: Works with existing PostgreSQL configurations

This feature provides a robust solution for managing manga chapter parts while maintaining full data integrity and revert capabilities.
