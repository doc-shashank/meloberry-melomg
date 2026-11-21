using Melomg.Domain.Errors;

namespace Melomg.Domain.Common;

public enum ReorderMode
{
    Exchange,
    Shift
}

public interface IIndexedEntity
{
    string Id { get; }
    int Index { get; }
    void SetIndexInternal(int index);
}

/// <summary>
/// Shared 1-based indexing and reordering invariant logic for document figures.
/// </summary>
internal static class IndexedListReorderer
{
    public static Result<int> InsertAt<T>(List<T> items, T newItem, int? targetIndex) where T : IIndexedEntity
    {
        int insertIndex = targetIndex ?? (items.Count + 1);
        if (insertIndex < 1 || insertIndex > items.Count + 1)
            return IndexErrors.OutOfIndexBounds(insertIndex, 1, items.Count + 1);

        foreach (var existing in items)
        {
            if (existing.Index >= insertIndex)
                existing.SetIndexInternal(existing.Index + 1);
        }

        newItem.SetIndexInternal(insertIndex);
        items.Add(newItem);
        SortInPlace(items);
        return insertIndex;
    }

    public static Result RemoveAndShiftUp<T>(List<T> items, string id, Error? notFoundError = null) where T : IIndexedEntity
    {
        var target = items.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (target is null)
            return notFoundError ?? IndexErrors.ItemNotFound(id);

        int removedIndex = target.Index;
        items.Remove(target);
        foreach (var remaining in items)
        {
            if (remaining.Index > removedIndex)
                remaining.SetIndexInternal(remaining.Index - 1);
        }

        SortInPlace(items);
        return Result.Success();
    }

    public static Result Reorder<T>(List<T> items, string id, int targetIndex, ReorderMode mode, Error? notFoundError = null)
        where T : IIndexedEntity
    {
        var source = items.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (source is null)
            return notFoundError ?? IndexErrors.ItemNotFound(id);

        if (targetIndex < 1 || targetIndex > items.Count)
            return IndexErrors.OutOfIndexBounds(targetIndex, 1, items.Count);

        int originalIndex = source.Index;
        if (originalIndex == targetIndex)
            return Result.Success();

        if (mode == ReorderMode.Exchange)
        {
            items.FirstOrDefault(x => x.Index == targetIndex)?.SetIndexInternal(originalIndex);
        }
        else if (originalIndex < targetIndex)
        {
            foreach (var item in items)
                if (item.Index > originalIndex && item.Index <= targetIndex)
                    item.SetIndexInternal(item.Index - 1);
        }
        else
        {
            foreach (var item in items)
                if (item.Index >= targetIndex && item.Index < originalIndex)
                    item.SetIndexInternal(item.Index + 1);
        }

        source.SetIndexInternal(targetIndex);
        SortInPlace(items);
        return Result.Success();
    }

    public static void SortInPlace<T>(List<T> items) where T : IIndexedEntity =>
        items.Sort((a, b) => a.Index.CompareTo(b.Index));
}
