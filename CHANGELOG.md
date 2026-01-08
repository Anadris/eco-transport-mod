# Changelog

## v1.3.0

### Added
- **New Skills System**: Introduces Transporter profession and Logistics specialty
  - `TransporterSkill`: Profession skill, foundation for transportation specialists
  - `LogisticsSkill`: Specialty skill with powerful bonuses
    - Multiplicative bonuses: -20% to -50% resource cost reduction (levels 1-7) TBM
    - Additive bonuses: +50% to +80% output increase (levels 1-7) TBM
    - Carry weight bonus: +1000kg per level (up to +7000kg at level 7) TBM
    - Movement speed bonus: +2% per level (up to +14% at level 7) TBM
  - Skill book recipe craftable at Research Table
  - Custom icons for skills via Unity AssetBundle
- Usage statistics tracking: all transport commands now track usage by player with timestamps
- `/transport stats` command: displays comprehensive usage statistics
  - Shows total command usage (all time and last 24 hours)
  - Breaks down statistics by command type (panel, find, detail, refresh, info, stats)
- All commands now record usage with timestamp: panel, find, detail, refresh, info, stats

### Technical
- Added Unity AssetBundle support for custom skill icons
- Bundle name: `ecotransport.unity3d` in `/Assets/` folder
- Skill bonuses ready for future Harmony patch integration

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
