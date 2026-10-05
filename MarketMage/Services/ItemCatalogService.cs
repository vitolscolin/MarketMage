using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MarketMage.Models;

namespace MarketMage.Services;

public sealed class ItemCatalogService
{
    private readonly IDataManager dataManager;

    public ItemCatalogService(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public IReadOnlyList<ItemCatalogEntry> GetItemCatalog()
    {
        return dataManager.GetExcelSheet<Item>()
            .Where(item => item.RowId > 0)
            .Where(item => item.ItemSearchCategory.RowId > 0 && !item.IsUntradable)
            .Select(item => new ItemCatalogEntry
            {
                ItemId = item.RowId,
                CanBeHq = item.CanBeHq,
                Name = item.Name.ToString(),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .OrderBy(item => item.Name)
            .ThenBy(item => item.ItemId)
            .ToList();
    }
}
