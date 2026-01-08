# Changelog

## v1.3.0

### Added
- **Multi-Stop Route Optimization**: New route planning system for efficient deliveries
  - `/transport route [maxRoutes]` command: displays optimized multi-stop delivery routes
  - Automatically chains opportunities where sell location becomes next buy location
  - Routes sorted by total profit and number of stops
  - Shows cumulative distance and profit for each stop
  - Displays efficiency ratio (profit per meter traveled)
  - Prevents circular routes by tracking visited stores
  - Requires Logistics skill level 1

- **New Skills System**: Introduces Transporter profession and Logistics specialty
  - `TransporterSkill`: Profession skill, foundation for transportation specialists
  - `LogisticsSkill`: Specialty skill with powerful bonuses
    - **Level 1 required to use all `/transport` commands** (panel, find, detail, refresh, stats, info, route)
    - Multiplicative bonuses: -20% to -50% resource cost reduction (levels 1-7) TBM
    - Additive bonuses: +50% to +80% output increase (levels 1-7) TBM
    - Carry weight bonus: +1000kg per level (up to +7000kg at level 7) TBM
    - Movement speed bonus: +2% per level (up to +14% at level 7) TBM
  - Skill book recipe craftable at Research Table
  - Custom icons for skills via Unity AssetBundle
- **Packing Table**: New craft table for logistics operations
  - Requires Logistics skill level 1
  - Crafted at Workbench with 20 Wood + 50 Wood Pulp
  - Supports Basic Upgrade modules
  - Located in Ecopedia: Work Stations > Craft Tables
- **New Packaging Items**: Complete packaging supply chain
  - **Wood Paste** (Logistics lvl 1): 2 Wood + 2 Dirt → 5 Wood Paste
  - **Tape Roll** (Logistics lvl 1): 1 Fruit + 2 Plant Fibers + 2 Dirt → 1 Tape Roll
  - **Shredded Cardboard** (Logistics lvl 1): 1 Wood Paste + 1 Wood Pulp + 1 Plant Fibers → 1 Shredded Cardboard
  - **Package Box** (Logistics lvl 2): 3 Shredded Cardboard + 1 Tape Roll → 1 Package Box
  - All recipes craftable at Packing Table
- Usage statistics tracking: all transport commands now track usage by player with timestamps
- `/transport stats` command: displays comprehensive usage statistics
  - Shows total command usage (all time and last 24 hours)
  - Breaks down statistics by command type (panel, find, detail, refresh, info, stats)
- All commands now record usage with timestamp: panel, find, detail, refresh, info, stats
- **Server Configuration**: Configurable skill requirement
  - `REQUIRE_LOGISTICS_SKILL` constant in code to enable/disable Logistics skill requirement
  - Set to `true` by default (requires Logistics level 1)
  - Set to `false` to allow all players to use commands without skill
  - Configuration located at top of EcoTransportMod.cs for easy access

### Technical
- Added Unity AssetBundle support for custom skill and item icons
- Bundle name: `ecotransport.unity3d` in `/Assets/` folder
- Skill bonuses ready for future Harmony patch integration
- New packaging items with proper tags for filtering and categorization
- Configurable skill requirement via `EcoTransportModConfig.REQUIRE_LOGISTICS_SKILL` constant

## v1.2.0

### Added
- Own store filter: players no longer see opportunities where they would sell to their own store
- Total cost display: shows the total purchase cost for each opportunity
- Affordability indicator: total cost is displayed in green if the player can afford it, light red if not
- Buyer balance check: MaxQuantity now accounts for the buyer store's available balance to pay for items
- Storage capacity check: verifies if the sell-to store has sufficient storage space to accept items
  - Displays storage status in all views (panel, find, detail)
  - Calculates real capacity based on item stack size and available slots
  - Shows warnings when storage is limited or full
  - Color-coded indicators: red for full storage, orange for limited capacity, green for sufficient space


## v1.1.0

### Added
- Currency filter: opportunities now only show trades with matching buy/sell currency
- Currency display: currency name shown next to all prices in UI panels
- Currency tooltip: hoverable currency links with native Eco tooltip
- Enhance UI readability overall

### Removed legacy command
- `/transport stats` command
- `/transport search` command
- `/transport export` command
