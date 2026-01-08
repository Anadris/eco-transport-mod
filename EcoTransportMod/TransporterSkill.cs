// EcoTransportMod - Transporter Skill
// Professional skill - parent for Logistics specialty

namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using Eco.Core.Items;
    using Eco.Gameplay.Components;
    using Eco.Gameplay.DynamicValues;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Skills;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Localization;
    using Eco.Shared.Serialization;
    using Eco.Shared.Utils;

    /// <summary>
    /// Transporter professional skill - Parent skill for transportation specialties
    /// This is a basic profession with minimal bonuses, designed to be a prerequisite for Logistics
    /// </summary>
    [Serialized]
    [LocDisplayName("Transporter")]
    [LocDescription("Foundation skill for transportation and logistics specialists. Unlock the Logistics specialty to gain powerful bonuses.")]
    [Tag("Profession")]
    public partial class TransporterSkill : Skill
    {
        public override string Title { get { return Localizer.DoStr("Transporter"); } }

        // Minimal multiplicative bonuses for parent skill
        public static MultiplicativeStrategy MultiplicativeStrategy =
            new MultiplicativeStrategy(new float[] {
                1, 1, 1, 1, 1, 1, 1, 1,
            });
        public override MultiplicativeStrategy MultiStrategy => MultiplicativeStrategy;

        // Minimal additive bonuses for parent skill
        public static AdditiveStrategy AdditiveStrategy =
            new AdditiveStrategy(new float[] {
                0, 0, 0, 0, 0, 0, 0, 0,
            });
        public override AdditiveStrategy AddStrategy => AdditiveStrategy;

        public override int MaxLevel { get { return 7; } }
        public override int Tier { get { return 1; } }
    }
}
