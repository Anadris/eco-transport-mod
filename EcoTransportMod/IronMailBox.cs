// EcoTransportMod - Iron Mail Box
// Craft table for mail and package operations requiring Logistics skill level 2

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
    [RequireComponent(typeof(PropertyAuthComponent))]
    [RequireComponent(typeof(MinimapComponent))]
    [RequireComponent(typeof(LinkComponent))]
    [RequireComponent(typeof(HousingComponent))]
    [RequireComponent(typeof(OccupancyRequirementComponent))]
    [RequireComponent(typeof(ForSaleComponent))]
    [RequireComponent(typeof(PublicStorageComponent))]
    [Tag("Usable")]
    [Ecopedia("Items", "Storage", subPageName: "Iron Mail Box")]

    public partial class IronMailBoxObject : WorldObject, IRepresentsItem
    {
        public virtual Type RepresentedItemType => typeof(IronMailBoxItem);
        public override LocString DisplayName => Localizer.DoStr("Iron Mail Box");

        static IronMailBoxObject()
        {
            // Occupancy: 1 bloc de large (x), 2 blocs de haut (y), 1 bloc de profondeur (z)
            // Offset Unity: (0, 0, 0)
            WorldObject.AddOccupancy<IronMailBoxObject>(new List<BlockOccupancy>(){
                // Niveau 0 (sol)
                new BlockOccupancy(new Vector3i(0, 0, 0)),

                // Niveau 1
                new BlockOccupancy(new Vector3i(0, 1, 0))
            });
        }

        protected override void Initialize()
        {
            this.ModsPreInitialize();
            this.GetComponent<MinimapComponent>().SetCategory(Localizer.DoStr("Crafting"));
            this.GetComponent<HousingComponent>().HomeValue = IronMailBoxItem.homeValue;
            this.ModsPostInitialize();
        }

        protected override void PostInitialize()
        {
            base.PostInitialize();

            // Configure public storage with 10 slots
            var storage = this.GetComponent<PublicStorageComponent>();
            storage.Initialize(10);
        }

        partial void ModsPreInitialize();
        partial void ModsPostInitialize();
    }

    [Serialized]
    [LocDisplayName("Iron Mail Box")]
    [LocDescription("An iron mailbox for storing mail and packages. Requires Logistics skill level 4.")]
    [Weight(1000)]
    public partial class IronMailBoxItem : WorldObjectItem<IronMailBoxObject>, IPersistentData
    {
        protected override OccupancyContext GetOccupancyContext => new SideAttachedContext(0 | DirectionAxisFlags.Down, WorldObject.GetOccupancyInfo(this.WorldObjectType));

        public override HomeFurnishingValue HomeValue => homeValue;
        public static readonly HomeFurnishingValue homeValue = new HomeFurnishingValue()
        {
            ObjectName = typeof(IronMailBoxObject).UILink(),
            Category = HousingConfig.GetRoomCategory("Outdoor"),
            TypeForRoomLimit = Localizer.DoStr(""),
            BaseValue = 4,
        };

        [Serialized, SyncToView, NewTooltipChildren(CacheAs.Instance, flags: TTFlags.AllowNonControllerTypeForChildren)]
        public object PersistentData { get; set; }
    }

    /// <summary>
    /// Recipe definition for IronMailBox - requires Logistics skill level 4
    /// Crafted at PackingTable with 20 Package Box + 2 Iron Bar
    /// </summary>
    [RequiresSkill(typeof(LogisticsSkill), 4)]
    public partial class IronMailBoxRecipe : RecipeFamily
    {
        public IronMailBoxRecipe()
        {
            var recipe = new Recipe();
            recipe.Init(
                name: "IronMailBox",  //noloc
                displayName: Localizer.DoStr("Iron Mail Box"),

                // Ingredients: 20 Package Box + 2 Iron Bar
                ingredients: new List<IngredientElement>
                {
                    new IngredientElement(typeof(PackageBoxItem), 20, typeof(LogisticsSkill)),
                    new IngredientElement(typeof(IronBarItem), 2, typeof(LogisticsSkill)),
                },

                // Output: Iron Mail Box
                items: new List<CraftingElement>
                {
                    new CraftingElement<IronMailBoxItem>()
                });

            this.Recipes = new List<Recipe> { recipe };
            this.ExperienceOnCraft = 2;

            // Labor required
            this.LaborInCalories = CreateLaborInCaloriesValue(200, typeof(LogisticsSkill));

            // Crafting time
            this.CraftMinutes = CreateCraftTimeValue(beneficiary: typeof(IronMailBoxRecipe), start: 2, skillType: typeof(LogisticsSkill));

            this.ModsPreInitialize();
            this.Initialize(displayText: Localizer.DoStr("Iron Mail Box"), recipeType: typeof(IronMailBoxRecipe));
            this.ModsPostInitialize();

            // Crafted at Packing Table
            CraftingComponent.AddRecipe(tableType: typeof(PackingTableObject), recipeFamily: this);
        }

        partial void ModsPreInitialize();
        partial void ModsPostInitialize();
    }
}
