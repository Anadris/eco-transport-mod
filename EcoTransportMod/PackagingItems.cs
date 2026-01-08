// EcoTransportMod - Packaging Items
// Items for creating packages and shipping materials

namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using Eco.Core.Items;
    using Eco.Gameplay.Blocks;
    using Eco.Gameplay.Components;
    using Eco.Gameplay.DynamicValues;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Objects;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Skills;
    using Eco.Gameplay.Systems;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Localization;
    using Eco.Shared.Serialization;
    using Eco.Shared.Utils;
    using Eco.World;
    using Eco.World.Blocks;
    using Eco.Gameplay.Pipes;
    using Eco.Core.Controller;
    using Eco.Gameplay.Items.Recipes;

    // =====================================================
    // WOOD PASTE
    // =====================================================

    [Serialized]
    [LocDisplayName("Wood Paste")]
    [LocDescription("A sticky paste made from wood and dirt, useful for binding materials together.")]
    [Weight(100)]
    [MaxStackSize(100)]
    [Ecopedia("Items", "Products", createAsSubPage: true)]
    [Tag("Packaging Material")]
    [Tag("Craftable")]
    public partial class WoodPasteItem : Item
    {
    }

    [RequiresSkill(typeof(LogisticsSkill), 1)]
    [Ecopedia("Items", "Products", subPageName: "Wood Paste Item")]
    public partial class WoodPasteRecipe : RecipeFamily
    {
        public WoodPasteRecipe()
        {
            var recipe = new Recipe();
            recipe.Init(
                name: "WoodPaste",  //noloc
                displayName: Localizer.DoStr("Wood Paste"),

                ingredients: new List<IngredientElement>
                {
                    new IngredientElement("Wood", 2, typeof(LogisticsSkill)),
                    new IngredientElement(typeof(DirtItem), 2, typeof(LogisticsSkill)),
                },

                items: new List<CraftingElement>
                {
                    new CraftingElement<WoodPasteItem>(5)
                });

            this.Recipes = new List<Recipe> { recipe };
            this.ExperienceOnCraft = 1;

            this.LaborInCalories = CreateLaborInCaloriesValue(50, typeof(LogisticsSkill));
            this.CraftMinutes = CreateCraftTimeValue(beneficiary: typeof(WoodPasteRecipe), start: 0.5f, skillType: typeof(LogisticsSkill));

            this.ModsPreInitialize();
            this.Initialize(displayText: Localizer.DoStr("Wood Paste"), recipeType: typeof(WoodPasteRecipe));
            this.ModsPostInitialize();

            CraftingComponent.AddRecipe(tableType: typeof(PackingTableObject), recipeFamily: this);
        }

        partial void ModsPreInitialize();
        partial void ModsPostInitialize();
    }

    // =====================================================
    // TAPE ROLL
    // =====================================================

    [Serialized]
    [LocDisplayName("Tape Roll")]
    [LocDescription("A roll of adhesive tape made from plant fibers and fruit extracts.")]
    [Weight(50)]
    [MaxStackSize(100)]
    [Ecopedia("Items", "Products", createAsSubPage: true)]
    [Tag("Packaging Material")]
    [Tag("Craftable")]
    public partial class TapeRollItem : Item
    {
    }

    [RequiresSkill(typeof(LogisticsSkill), 1)]
    [Ecopedia("Items", "Products", subPageName: "Tape Roll Item")]
    public partial class TapeRollRecipe : RecipeFamily
    {
        public TapeRollRecipe()
        {
            var recipe = new Recipe();
            recipe.Init(
                name: "TapeRoll",  //noloc
                displayName: Localizer.DoStr("Tape Roll"),

                ingredients: new List<IngredientElement>
                {
                    new IngredientElement("Fruit", 1, typeof(LogisticsSkill)),
                    new IngredientElement(typeof(PlantFibersItem), 2, typeof(LogisticsSkill)),
                    new IngredientElement(typeof(DirtItem), 2, typeof(LogisticsSkill)),
                },

                items: new List<CraftingElement>
                {
                    new CraftingElement<TapeRollItem>()
                });

            this.Recipes = new List<Recipe> { recipe };
            this.ExperienceOnCraft = 1;

            this.LaborInCalories = CreateLaborInCaloriesValue(75, typeof(LogisticsSkill));
            this.CraftMinutes = CreateCraftTimeValue(beneficiary: typeof(TapeRollRecipe), start: 1f, skillType: typeof(LogisticsSkill));

            this.ModsPreInitialize();
            this.Initialize(displayText: Localizer.DoStr("Tape Roll"), recipeType: typeof(TapeRollRecipe));
            this.ModsPostInitialize();

            CraftingComponent.AddRecipe(tableType: typeof(PackingTableObject), recipeFamily: this);
        }

        partial void ModsPreInitialize();
        partial void ModsPostInitialize();
    }

    // =====================================================
    // SHREDDED CARDBOARD
    // =====================================================

    [Serialized]
    [LocDisplayName("Shredded Cardboard")]
    [LocDescription("Cardboard material made from wood paste and plant fibers, ready to be formed into boxes.")]
    [Weight(75)]
    [MaxStackSize(100)]
    [Ecopedia("Items", "Products", createAsSubPage: true)]
    [Tag("Packaging Material")]
    [Tag("Craftable")]
    public partial class ShreddedCardboardItem : Item
    {
    }

    [RequiresSkill(typeof(LogisticsSkill), 1)]
    [Ecopedia("Items", "Products", subPageName: "Shredded Cardboard Item")]
    public partial class ShreddedCardboardRecipe : RecipeFamily
    {
        public ShreddedCardboardRecipe()
        {
            var recipe = new Recipe();
            recipe.Init(
                name: "ShreddedCardboard",  //noloc
                displayName: Localizer.DoStr("Shredded Cardboard"),

                ingredients: new List<IngredientElement>
                {
                    new IngredientElement(typeof(WoodPasteItem), 1, typeof(LogisticsSkill)),
                    new IngredientElement(typeof(WoodPulpItem), 1, typeof(LogisticsSkill)),
                    new IngredientElement(typeof(PlantFibersItem), 1, typeof(LogisticsSkill)),
                },

                items: new List<CraftingElement>
                {
                    new CraftingElement<ShreddedCardboardItem>()
                });

            this.Recipes = new List<Recipe> { recipe };
            this.ExperienceOnCraft = 1;

            this.LaborInCalories = CreateLaborInCaloriesValue(100, typeof(LogisticsSkill));
            this.CraftMinutes = CreateCraftTimeValue(beneficiary: typeof(ShreddedCardboardRecipe), start: 1.5f, skillType: typeof(LogisticsSkill));

            this.ModsPreInitialize();
            this.Initialize(displayText: Localizer.DoStr("Shredded Cardboard"), recipeType: typeof(ShreddedCardboardRecipe));
            this.ModsPostInitialize();

            CraftingComponent.AddRecipe(tableType: typeof(PackingTableObject), recipeFamily: this);
        }

        partial void ModsPreInitialize();
        partial void ModsPostInitialize();
    }

    // =====================================================
    // PACKAGE BOX
    // =====================================================

    [Serialized]
    [LocDisplayName("Package Box")]
    [LocDescription("A sturdy cardboard box sealed with tape, perfect for shipping goods efficiently.")]
    [Weight(150)]
    [MaxStackSize(50)]
    [Ecopedia("Items", "Products", createAsSubPage: true)]
    [Tag("Packaging")]
    public partial class PackageBoxItem : Item
    {
    }

    [RequiresSkill(typeof(LogisticsSkill), 2)]
    [Ecopedia("Items", "Products", subPageName: "Package Box Item")]
    public partial class PackageBoxRecipe : RecipeFamily
    {
        public PackageBoxRecipe()
        {
            var recipe = new Recipe();
            recipe.Init(
                name: "PackageBox",  //noloc
                displayName: Localizer.DoStr("Package Box"),

                ingredients: new List<IngredientElement>
                {
                    new IngredientElement(typeof(ShreddedCardboardItem), 3, typeof(LogisticsSkill)),
                    new IngredientElement(typeof(TapeRollItem), 1, typeof(LogisticsSkill)),
                },

                items: new List<CraftingElement>
                {
                    new CraftingElement<PackageBoxItem>()
                });

            this.Recipes = new List<Recipe> { recipe };
            this.ExperienceOnCraft = 2;

            this.LaborInCalories = CreateLaborInCaloriesValue(150, typeof(LogisticsSkill));
            this.CraftMinutes = CreateCraftTimeValue(beneficiary: typeof(PackageBoxRecipe), start: 2f, skillType: typeof(LogisticsSkill));

            this.ModsPreInitialize();
            this.Initialize(displayText: Localizer.DoStr("Package Box"), recipeType: typeof(PackageBoxRecipe));
            this.ModsPostInitialize();

            CraftingComponent.AddRecipe(tableType: typeof(PackingTableObject), recipeFamily: this);
        }

        partial void ModsPreInitialize();
        partial void ModsPostInitialize();
    }
}
