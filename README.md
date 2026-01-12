# ECO Transport Mod
Mod for the game Eco, edited by StangeLoop studio.
[https://play.eco/](https://play.eco/)

![EcoBanner](https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/382310/669bd5d9c8d2469a776d81011670f2889fac86b0/header.jpg)

A useful and lightweight mod to help players find transport opportunities.

You can make money out of buying / selling goods, but sometimes it is hard to track these opportunities.
This mod helps you track prices and filter buy / sell orders where you can make profit.

## Easy install

Also available from [mod.io](https://mod.io/g/eco/m/eco-transport#description)

Copy `EcoTransportMod` __folder__ on your server into your mods folder `\Server\Mods\UserCode`

Reboot your server and it's done !

## Features

- Find profitable trade opportunities across all stores
- Find the most profitable delivery routes with multiple stops
- Filter by matching currency (buy and sell must use same currency)
- Native Eco UI with clickable links for stores, items and currencies
- Distance calculation between buy/sell locations
- Check you can afford the investment
- Check buyer's can afford the delivery, and have available storage for the items
- Check buyer's store is accessible to everyone
- Usage statistics tracking per player
- **Configurable skill requirement** for server admins
- **Skills System**: Experimental Transporter profession with Logistics specialty skill
- **Packing Table & Packaging Items**: Craft packaging materials for logistics operations
- **Mail Boxes**: Outdoor Housing items with 5 to 10 storage slots and 3 to 4 housing points
More content to come on profession in next updates

## Usage

See available commands by typing `/transport info`

### Available commands

| Command | Description |
| ------- | ----------- |
| `/transport panel` | Open the main UI panel |
| `/transport panel <n>` | Open the main UI panel (max 200 items) |
| `/transport route` | Find optimized multi-stop delivery routes |
| `/transport find <product>` | Search with UI panel |
| `/transport detail <product>` | Detailed product analysis |
| `/transport refresh` | Refresh market data |
| `/transport stats` | Show usage statistics by player |
| `/transport info` | Show help |

**Note**: By default, all commands require Logistics skill level 1. Server admins can disable this requirement (see Configuration section below).



![EcoTransport-panel](EcoTransport-panel1.jpg)

## Configuration

Server admins can customize the mod by editing the configuration section at the top of `EcoTransportMod.cs`:

```csharp
// ═══════════════════════════════════════════════════════════════
// CONFIGURATION - Modify these values to customize the mod
// ═══════════════════════════════════════════════════════════════
public static class EcoTransportModConfig
{
    /// <summary>
    /// Set to true to require Logistics skill level 1 for all /transport commands
    /// Set to false to allow all players to use commands without skill requirement
    /// </summary>
    public const bool REQUIRE_LOGISTICS_SKILL = true;
}
```

**To disable skill requirement**:
1. Open `EcoTransportMod/EcoTransportMod.cs`
2. Change `REQUIRE_LOGISTICS_SKILL = true;` to `REQUIRE_LOGISTICS_SKILL = false;`
3. Restart your server

This allows all players to use `/transport` commands without needing the Logistics skill.

## Contributions

Contributions and bug reports are welcome !

### Future Plan
- Improve UI experience (resize window, components -> buttons, select, tables)
- Add filtering on player's skill production & player's own materials
- Add config for transport method (cart, truck)
- Add transportation info (number of trips required based on carry capacity)
- Harmony patches to apply Logistics skill bonuses to recipes
- [Public Feature] Add feature DeliveryOpportunity which allow any user to place an order with delivery, and transporter (only) to answer it. Would take benefit of MailBoxes and notifications.
