namespace PS5Emulator.Memory;

public class PageTable
{
    // Research-level paging support. A real PS5 uses 4-level paging with 4K pages.
    private readonly Dictionary<ulong, ulong> _mappings = new();

    public void Map(ulong virtualAddress, ulong physicalAddress)
    {
        _mappings[virtualAddress] = physicalAddress;
    }

    public void Unmap(ulong virtualAddress)
    {
        _mappings.Remove(virtualAddress);
    }

    public bool TryTranslate(ulong virtualAddress, out ulong physicalAddress)
    {
        return _mappings.TryGetValue(virtualAddress & ~(ulong)(PageTable.PageSize - 1), out physicalAddress);
    }

    public const int PageSize = MemoryManager.PageSize;
}
