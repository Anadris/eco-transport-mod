# Changelog

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
