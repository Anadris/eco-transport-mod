// EcoTransportMod - Packing Table
// Craft table for logistics operations requiring Logistics skill level 1

namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using Eco.Core.Items;
    using Eco.Gameplay.Blocks;
    using Eco.Gameplay.Components;
    using Eco.Gameplay.Components.Auth;
    using Eco.Gameplay.DynamicValues;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Housing;
    using Eco.Gameplay.Interactions;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Modules;
    using Eco.Gameplay.Minimap;
    using Eco.Gameplay.Objects;
    using Eco.Gameplay.Occupancy;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Property;
    using Eco.Gameplay.Skills;
    using Eco.Gameplay.Systems;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Gameplay.Pipes.LiquidComponents;
    using Eco.Gameplay.Pipes.Gases;
    using Eco.Shared;
    using Eco.Shared.Math;
    using Eco.Shared.Localization;
    using Eco.Shared.Serialization;
    using Eco.Shared.Utils;
    using Eco.Shared.View;
    using Eco.Shared.Items;
    using Eco.Shared.Networking;
    using Eco.Gameplay.Pipes;
    using Eco.World.Blocks;
    using Eco.Gameplay.Housing.PropertyValues;
    using Eco.Gameplay.Civics.Objects;
    using Eco.Gameplay.Settlements;
    using Eco.Gameplay.Systems.NewTooltip;
    using Eco.Core.Controller;
    using Eco.Core.Utils;
    using Eco.Gameplay.Components.Storage;
    using static Eco.Gameplay.Housing.PropertyValues.HomeFurnishingValue;
    using Eco.Gameplay.Items.Recipes;

    [Serialized]
    [RequireComponent(typeof(OnOffComponent))]
    [RequireComponent(typeof(PropertyAuthComponent))]
    [RequireComponent(typeof(MinimapComponent))]
    [RequireComponent(typeof(LinkComponent))]
    [RequireComponent(typeof(CraftingComponent))]
    [RequireComponent(typeof(HousingComponent))]
    [RequireComponent(typeof(OccupancyRequirementComponent))]
    [RequireComponent(typeof(PluginModulesComponent))]
    [RequireComponent(typeof(ForSaleComponent))]
    [RequireComponent(typeof(RoomRequirementsComponent))]
    [RequireRoomContainment]
    [RequireRoomVolume(18)]
    [RequireRoomMaterialTier(0.8f)]
    [Tag("Usable")]
    [Ecopedia("Work Stations", "Craft Tables", subPageName: "Packing Table Item")]
    [Tag(nameof(SurfaceTags.HasTableSurface))]

    public partial class PackingTableObject : WorldObject, IRepresentsItem
    {
        public virtual Type RepresentedItemType => typeof(PackingTableItem);
        public override LocString DisplayName => Localizer.DoStr("Packing Table");
        public override TableTextureMode TableTexture => TableTextureMode.Wood;

        static PackingTableObject()
        {
            // Occupancy: 1 bloc de large (x), 3 blocs de haut (y), 2 blocs de profondeur (z)
            // Offset Unity: (0, 0, 0)
            WorldObject.AddOccupancy<PackingTableObject>(new List<BlockOccupancy>(){
                // Niveau 0 (sol) - profondeur z=0 et z=1
                new BlockOccupancy(new Vector3i(0, 0, 0)),
                new BlockOccupancy(new Vector3i(0, 0, 1)),

                // Niveau 1
                new BlockOccupancy(new Vector3i(0, 1, 0)),
                new BlockOccupancy(new Vector3i(0, 1, 1)),

                // Niveau 2
                new BlockOccupancy(new Vector3i(0, 2, 0)),
                new BlockOccupancy(new Vector3i(0, 2, 1))
            });
        }

        protected override void Initialize()
        {
            this.ModsPreInitialize();
            this.GetComponent<MinimapComponent>().SetCategory(Localizer.DoStr("Crafting"));
            this.GetComponent<HousingComponent>().HomeValue = PackingTableItem.homeValue;
            this.ModsPostInitialize();
        }

        partial void ModsPreInitialize();
        partial void ModsPostInitialize();
    }

    [Serialized]
    [LocDisplayName("Packing Table")]
    [LocDescription("A specialized table for packing and preparing goods for efficient transportation. Requires Logistics skill.")]
    [Ecopedia("Work Stations", "Craft Tables", createAsSubPage: true)]
    [Weight(2000)]
    [AllowPluginModules(Tags = new[] { "BasicUpgrade" }, ItemTypes = new[] { typeof(LogisticsBasicUpgradeItem) })]
    public partial class PackingTableItem : WorldObjectItem<PackingTableObject>, IPersistentData
    {
        protected override OccupancyContext GetOccupancyContext => new SideAttachedContext(0 | DirectionAxisFlags.Down, WorldObject.GetOccupancyInfo(this.WorldObjectType));

        public override HomeFurnishingValue HomeValue => homeValue;
        public static readonly HomeFurnishingValue homeValue = new HomeFurnishingValue()
        {
            ObjectName = typeof(PackingTableObject).UILink(),
            Category = HousingConfig.GetRoomCategory("Industrial"),
            TypeForRoomLimit = Localizer.DoStr(""),
        };

        [Serialized, SyncToView, NewTooltipChildren(CacheAs.Instance, flags: TTFlags.AllowNonControllerTypeForChildren)]
        public object PersistentData { get; set; }
    }

    /// <summary>
    /// Recipe definition for PackingTable - requires Logistics skill level 1
    /// Crafted at Workbench with 20 Wood + 50 Wood Pulp
    /// </summary>
    [RequiresSkill(typeof(LogisticsSkill), 1)]
    [Ecopedia("Work Stations", "Craft Tables", subPageName: "Packing Table Item")]
    public partial class PackingTableRecipe : RecipeFamily
    {
        public PackingTableRecipe()
        {
            var recipe = new Recipe();
            recipe.Init(
                name: "PackingTable",  //noloc
                displayName: Localizer.DoStr("Packing Table"),

                // Ingredients: 20 Wood + 50 Wood Pulp
                ingredients: new List<IngredientElement>
                {
                    new IngredientElement("Wood", 20, typeof(LogisticsSkill)),
                    new IngredientElement(typeof(WoodPulpItem), 50, typeof(LogisticsSkill)),
                },

                // Output: Packing Table
                items: new List<CraftingElement>
                {
                    new CraftingElement<PackingTableItem>()
                });

            this.Recipes = new List<Recipe> { recipe };
            this.ExperienceOnCraft = 2;

            // Labor required
            this.LaborInCalories = CreateLaborInCaloriesValue(200, typeof(LogisticsSkill));

            // Crafting time
            this.CraftMinutes = CreateCraftTimeValue(beneficiary: typeof(PackingTableRecipe), start: 2, skillType: typeof(LogisticsSkill));

            this.ModsPreInitialize();
            this.Initialize(displayText: Localizer.DoStr("Packing Table"), recipeType: typeof(PackingTableRecipe));
            this.ModsPostInitialize();

            // Crafted at Workbench
            CraftingComponent.AddRecipe(tableType: typeof(WorkbenchObject), recipeFamily: this);
        }

        partial void ModsPreInitialize();
        partial void ModsPostInitialize();
    }

    /// <summary>
    /// Basic upgrade item for Packing Table (placeholder for future upgrades)
    /// </summary>
    [Serialized]
    [LocDisplayName("Logistics Basic Upgrade")]
    [LocDescription("Basic upgrade module for logistics workstations.")]
    [Weight(1)]
    [Ecopedia("Items", "Upgrade Modules", createAsSubPage: true)]
    [Tag("Upgrade")]
    public partial class LogisticsBasicUpgradeItem : Item
    {
    }
}
