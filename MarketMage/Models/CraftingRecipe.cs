using System.Collections.Generic;

namespace MarketMage.Models;

public sealed class CraftingRecipe
{
    public uint RecipeId { get; init; }
    public string CraftJob { get; init; } = string.Empty;
    public int CraftLevel { get; init; }
    public bool CanHq { get; init; }
    public uint ResultItemId { get; init; }
    public int AmountResult { get; init; } = 1;
    public IReadOnlyList<RecipeIngredient> Ingredients { get; init; } = [];
}
