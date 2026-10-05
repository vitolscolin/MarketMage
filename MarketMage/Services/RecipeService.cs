using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MarketMage.Models;

namespace MarketMage.Services;

public sealed class RecipeService
{
    private readonly IDataManager dataManager;

    public RecipeService(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public HashSet<uint> GetCraftableItemIds() => dataManager.GetExcelSheet<Recipe>()
        .Where(r => r.ItemResult.RowId > 0).Select(r => r.ItemResult.RowId).ToHashSet();

    public IReadOnlyDictionary<uint, CraftingRecipe> GetRecipesForItems(IEnumerable<uint> itemIds) =>
        GetRecipeOptions(itemIds).ToDictionary(pair => pair.Key, pair => pair.Value[0]);

    public IReadOnlyDictionary<uint, IReadOnlyList<CraftingRecipe>> GetRecipeOptions(IEnumerable<uint> itemIds)
    {
        var wanted = itemIds.ToHashSet();
        return dataManager.GetExcelSheet<Recipe>()
            .Where(r => r.ItemResult.RowId > 0 && wanted.Contains(r.ItemResult.RowId))
            .Select(BuildRecipe)
            .GroupBy(r => r.ResultItemId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CraftingRecipe>)g.ToList());
    }

    private CraftingRecipe BuildRecipe(Recipe recipe)
    {
        return new CraftingRecipe
        {
            RecipeId = recipe.RowId,
            CraftJob = recipe.CraftType.Value.Name.ToString(),
            CraftLevel = recipe.RecipeLevelTable.Value.ClassJobLevel,
            CanHq = recipe.CanHq,
            ResultItemId = recipe.ItemResult.RowId,
            AmountResult = recipe.AmountResult <= 0 ? 1 : recipe.AmountResult,
            Ingredients = GetIngredients(recipe),
        };
    }

    private IReadOnlyList<RecipeIngredient> GetIngredients(Recipe recipe)
    {
        var ingredients = new List<RecipeIngredient>();

        for (var index = 0; index < recipe.Ingredient.Count; index++)
            AddIngredient(ingredients, recipe.Ingredient[index].RowId, recipe.AmountIngredient[index]);

        return ingredients;
    }

    private void AddIngredient(List<RecipeIngredient> ingredients, uint itemId, int quantity)
    {
        if (itemId == 0 || quantity <= 0)
            return;

        ingredients.Add(new RecipeIngredient
        {
            ItemId = itemId,
            ItemName = GetItemName(itemId),
            Quantity = quantity,
        });
    }

    private string GetItemName(uint itemId)
    {
        return dataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item)
            ? item.Name.ToString()
            : $"Item {itemId}";
    }
}
