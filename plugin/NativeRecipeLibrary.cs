// Reuse the shop's validated embedded resources and source-engine pool policy.
internal static class NativeRecipeLibrary
{
    internal static RecipeLibrary Current => LocalShop.Default.Library;
}
