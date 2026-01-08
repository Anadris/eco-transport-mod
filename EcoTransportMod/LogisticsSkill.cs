// EcoTransportMod - Logistics Specialty Skill
// Specialty skill under Transporter profession

namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Linq;
    using Eco.Core.Items;
    using Eco.Core.Utils;
    using Eco.Gameplay.Components;
    using Eco.Gameplay.DynamicValues;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Skills;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Localization;
    using Eco.Shared.Serialization;
    using Eco.Shared.Utils;
    using Eco.Gameplay.Items.Recipes;
    using Eco.Gameplay.Objects;

    /// <summary>
    /// Logistics specialty skill - Advanced transportation and supply chain management
    /// </summary>
    [Serialized]
    [LocDisplayName("Logistics")]
    [LocDescription("Logistics specialists excel at supply chain management and efficient resource distribution. Levels up by crafting related recipes.")]
    [Ecopedia("Professions", "Transporter", createAsSubPage: true)]
    [RequiresSkill(typeof(TransporterSkill), 0)]
    [Tag("Transporter Specialty")]
    [Tag("Specialty")]
    [Tag("Teachable")]
    public partial class LogisticsSkill : Skill
    {
        /// <summary>
        /// Called when the skill levels up
        /// </summary>
        public override void OnLevelUp(User user)
        {
            // Give Self Improvement XP for specialization
            user.Skillset.AddExperience(typeof(SelfImprovementSkill), 20, Localizer.DoStr("for leveling up another specialization."));

            // Update player's carrying capacity (from bonus)
            user.ChangedCarryWeight();
        }

        /// <summary>
        /// Called when the skill is reset
        /// </summary>
        public override void OnReset(User user)
        {
            // Update player's carrying capacity
            user.ChangedCarryWeight();
        }

        public static MultiplicativeStrategy MultiplicativeStrategy =
            new MultiplicativeStrategy(new float[] {
                1,
                1 - 0.2f,
                1 - 0.25f,
                1 - 0.3f,
                1 - 0.35f,
                1 - 0.4f,
                1 - 0.45f,
                1 - 0.5f,
            });
        public override MultiplicativeStrategy MultiStrategy => MultiplicativeStrategy;

        public static AdditiveStrategy AdditiveStrategy =
            new AdditiveStrategy(new float[] {
                0,
                0.5f,
                0.55f,
                0.6f,
                0.65f,
                0.7f,
                0.75f,
                0.8f,
            });
        public override AdditiveStrategy AddStrategy => AdditiveStrategy;

        public override int MaxLevel { get { return 7; } }
        public override int Tier { get { return 2; } }

        /// <summary>
        /// Calculate bonus carry weight for logistics skill
        /// This method should be called from a Harmony patch or talent system
        /// </summary>
        public static float GetCarryWeightBonus(User user)
        {
            var logisticsSkill = user?.Skillset?.GetSkill(typeof(LogisticsSkill));
            if (logisticsSkill == null) return 0f;

            // Each level adds 1000kg (1 ton) of carrying capacity
            return logisticsSkill.Level * 1000f;
        }

        /// <summary>
        /// Calculate movement speed multiplier for logistics skill
        /// This method should be called from a Harmony patch
        /// </summary>
        public static float GetMovementSpeedMultiplier(User user)
        {
            var logisticsSkill = user?.Skillset?.GetSkill(typeof(LogisticsSkill));
            if (logisticsSkill == null) return 1f;

            // Each level adds 2% movement speed (Level 7 = +14% speed)
            return 1f + (logisticsSkill.Level * 0.02f);
        }
    }

    /// <summary>
    /// Logistics Skill Book - Used to learn the Logistics specialty
    /// </summary>
    [Serialized]
    [Weight(1000)]
    [LocDisplayName("Logistics Skill Book")]
    [LocDescription("Skill Book to learn the Logistics specialty. Logistics specialists excel at supply chain management and efficient resource distribution.")]
    [Ecopedia("Items", "Skill Books", createAsSubPage: true)]
    public partial class LogisticsSkillBook : SkillBook<LogisticsSkill, LogisticsSkillScroll>
    {
    }

    /// <summary>
    /// Logistics Skill Scroll - Used to create Logistics Skill Books
    /// </summary>
    [Serialized]
    [Weight(100)]
    [LocDisplayName("Logistics Skill Scroll")]
    [LocDescription("Skill Scroll to create Logistics Skill Books.")]
    public partial class LogisticsSkillScroll : SkillScroll<LogisticsSkill, LogisticsSkillBook>
    {
    }

    /// <summary>
    /// Recipe to craft the Logistics Skill Book at Research Table
    /// </summary>
    [Ecopedia("Professions", "Transporter", subPageName: "Logistics Skill Book Item")]
    public partial class LogisticsSkillBookRecipe : RecipeFamily
    {
        public LogisticsSkillBookRecipe()
        {
            var recipe = new Recipe();
            recipe.Init(
                name: "Logistics",  //noloc
                displayName: Localizer.DoStr("Logistics Skill Book"),

                // Research paper ingredients
                ingredients: new List<IngredientElement>
                {
                    new IngredientElement(typeof(DendrologyResearchPaperAdvancedItem), 2),
                    new IngredientElement(typeof(CulinaryResearchPaperBasicItem), 2),
                },

                // Output: the skill book
                items: new List<CraftingElement>
                {
                    new CraftingElement<LogisticsSkillBook>()
                });

            this.Recipes = new List<Recipe> { recipe };

            // Labor required (600 calories) - using SelfImprovementSkill as base skill (available to all)
            this.LaborInCalories = CreateLaborInCaloriesValue(600, typeof(SelfImprovementSkill));

            // Crafting time: 5 minutes base - using SelfImprovementSkill as base skill
            this.CraftMinutes = CreateCraftTimeValue(
                beneficiary: typeof(LogisticsSkillBookRecipe),
                start: 5,
                skillType: typeof(SelfImprovementSkill));

            // Initialize the recipe
            this.ModsPreInitialize();
            this.Initialize(
                displayText: Localizer.DoStr("Logistics Skill Book"),
                recipeType: typeof(LogisticsSkillBookRecipe));
            this.ModsPostInitialize();

            // Register the recipe - crafted at Research Table
            CraftingComponent.AddRecipe(tableType: typeof(ResearchTableObject), recipeFamily: this);
        }

        /// <summary>
        /// Hook for mods to modify recipe before initialization
        /// </summary>
        partial void ModsPreInitialize();

        /// <summary>
        /// Hook for mods to modify recipe after initialization
        /// </summary>
        partial void ModsPostInitialize();
    }
}
