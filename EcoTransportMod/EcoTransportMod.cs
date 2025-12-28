// EcoTransportMod - Economy Data Mod for Eco 12.0.6
// Provides economy/market data via chat commands, native UI panels and file export

namespace Eco.Mods.EcoTransportMod
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using Eco.Core.Plugins.Interfaces;
    using Eco.Core.Utils;
    using Eco.Gameplay.Components;
    using Eco.Gameplay.Components.Store;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Objects;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Systems.Messaging.Chat.Commands;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.IoC;
    using Eco.Shared.Localization;
    using Eco.Shared.Text;
    using Eco.Shared.Utils;

    /// <summary>
    /// Main plugin class for EcoTransportMod
    /// </summary>
    public class EcoTransportModPlugin : IModKitPlugin, IInitializablePlugin, IShutdownablePlugin
    {
        public static EcoTransportModPlugin Instance { get; private set; }
        public static EconomyDataService DataService { get; private set; }

        public string GetCategory() => Localizer.DoStr("Economy");
        public string GetStatus() => Localizer.DoStr("Running");
        public override string ToString() => Localizer.DoStr("Eco Transport Mod");

        public void Initialize(TimedTask timer)
        {
            Instance = this;
            DataService = new EconomyDataService();
            DataService.Initialize();
        }

        public System.Threading.Tasks.Task ShutdownAsync()
        {
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Represents a single store offer (buy or sell)
    /// </summary>
    public class StoreOffer
    {
        public string ProductId { get; set; }
        public string ProductName { get; set; }
        public Type ItemType { get; set; }
        public float Price { get; set; }
        public int Quantity { get; set; }
        public bool IsBuyOffer { get; set; } // true = store wants to buy (we can sell), false = store sells (we can buy)
        public string StoreName { get; set; }
        public string OwnerName { get; set; }
        public WorldObject Store { get; set; }

        public LocString GetItemLink()
        {
            if (ItemType == null) return Localizer.DoStr(ProductName);
            var item = Item.Get(ItemType);
            return item?.UILink() ?? Localizer.DoStr(ProductName);
        }

        public LocString GetStoreLink()
        {
            return Store?.UILink() ?? Localizer.DoStr(StoreName);
        }
    }

    /// <summary>
    /// Represents a profitable trade opportunity between two stores
    /// </summary>
    public class TradeOpportunity
    {
        public StoreOffer BuyFrom { get; set; }  // Where we buy (store is selling)
        public StoreOffer SellTo { get; set; }   // Where we sell (store is buying)

        public float Margin => SellTo.Price - BuyFrom.Price;
        public int MaxQuantity => Math.Min(BuyFrom.Quantity, SellTo.Quantity);
        public float TotalProfit => Margin * MaxQuantity;
        public float ProfitPercent => BuyFrom.Price > 0 ? (Margin / BuyFrom.Price) * 100 : 0;

        /// <summary>
        /// Distance in blocks between buy and sell stores
        /// </summary>
        public float Distance
        {
            get
            {
                if (BuyFrom.Store == null || SellTo.Store == null) return 0;
                var pos1 = BuyFrom.Store.Position3i;
                var pos2 = SellTo.Store.Position3i;
                return (float)Math.Sqrt(
                    Math.Pow(pos2.X - pos1.X, 2) +
                    Math.Pow(pos2.Y - pos1.Y, 2) +
                    Math.Pow(pos2.Z - pos1.Z, 2));
            }
        }

        public LocString GetItemLink() => BuyFrom.GetItemLink();
        public string ProductName => BuyFrom.ProductName;
        public Type ItemType => BuyFrom.ItemType;

        public string ToJson()
        {
            return $"{{\"product\":\"{ProductName}\",\"buyPrice\":{BuyFrom.Price},\"buyStore\":\"{BuyFrom.StoreName}\",\"sellPrice\":{SellTo.Price},\"sellStore\":\"{SellTo.StoreName}\",\"margin\":{Margin},\"quantity\":{MaxQuantity},\"profit\":{TotalProfit},\"distance\":{Distance:F0}}}";
        }
    }

    /// <summary>
    /// Service that collects all store offers and finds profitable trade opportunities
    /// </summary>
    public class EconomyDataService
    {
        private readonly List<StoreOffer> _allOffers = new List<StoreOffer>();
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private DateTime _lastUpdate = DateTime.MinValue;

        public void Initialize()
        {
            RefreshAllData();
        }

        public void RefreshAllData()
        {
            _lock.EnterWriteLock();
            try
            {
                _allOffers.Clear();
                var allObjects = ServiceHolder<IWorldObjectManager>.Obj.All;

                foreach (var worldObject in allObjects)
                {
                    var store = worldObject.GetComponent<StoreComponent>();
                    if (store == null) continue;
                    CollectStoreOffers(store, worldObject);
                }

                _lastUpdate = DateTime.UtcNow;
            }
            catch (Exception) { }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private void CollectStoreOffers(StoreComponent store, WorldObject worldObject)
        {
            try
            {
                var offers = store.AllOffers;
                if (offers == null) return;

                var storeName = worldObject.Name ?? "Unknown Store";
                var ownerName = worldObject.Owners?.Name ?? "Unknown";

                foreach (var offer in offers)
                {
                    if (offer?.Stack?.Item == null) continue;
                    if (offer.Stack.Quantity <= 0) continue;

                    var itemType = offer.Stack.Item.GetType();

                    _allOffers.Add(new StoreOffer
                    {
                        ProductId = itemType.Name,
                        ProductName = offer.Stack.Item.DisplayName.ToString(),
                        ItemType = itemType,
                        Price = offer.Price,
                        Quantity = offer.Stack.Quantity,
                        IsBuyOffer = offer.Buying,
                        StoreName = storeName,
                        OwnerName = ownerName,
                        Store = worldObject
                    });
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Gets all profitable trade opportunities (all combinations where sell price > buy price)
        /// </summary>
        public List<TradeOpportunity> GetAllOpportunities()
        {
            _lock.EnterReadLock();
            try
            {
                var opportunities = new List<TradeOpportunity>();

                // Group offers by product
                var byProduct = _allOffers.GroupBy(o => o.ProductId);

                foreach (var productGroup in byProduct)
                {
                    var sellOffers = productGroup.Where(o => !o.IsBuyOffer).ToList(); // Stores selling (we buy from)
                    var buyOffers = productGroup.Where(o => o.IsBuyOffer).ToList();   // Stores buying (we sell to)

                    // Find all profitable combinations
                    foreach (var buyFrom in sellOffers)
                    {
                        foreach (var sellTo in buyOffers)
                        {
                            if (sellTo.Price > buyFrom.Price) // Profitable!
                            {
                                opportunities.Add(new TradeOpportunity
                                {
                                    BuyFrom = buyFrom,
                                    SellTo = sellTo
                                });
                            }
                        }
                    }
                }

                return opportunities
                    .OrderByDescending(o => o.TotalProfit)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public List<TradeOpportunity> SearchOpportunities(string searchTerm)
        {
            var allData = GetAllOpportunities();
            if (string.IsNullOrWhiteSpace(searchTerm))
                return allData;

            var term = searchTerm.ToLower();
            return allData
                .Where(o => o.ProductName.ToLower().Contains(term) ||
                           o.BuyFrom.StoreName.ToLower().Contains(term) ||
                           o.SellTo.StoreName.ToLower().Contains(term))
                .ToList();
        }

        public string ExportToJson()
        {
            var data = GetAllOpportunities();
            var sb = new StringBuilder();
            sb.AppendLine("[");
            for (int i = 0; i < data.Count; i++)
            {
                sb.Append("  ");
                sb.Append(data[i].ToJson());
                if (i < data.Count - 1)
                    sb.AppendLine(",");
                else
                    sb.AppendLine();
            }
            sb.AppendLine("]");
            return sb.ToString();
        }

        public DateTime LastUpdate => _lastUpdate;
    }

    /// <summary>
    /// UI Builder for creating native Eco info panels with styled content
    /// </summary>
    public static class TransportUIBuilder
    {
        private const int DefaultMaxItems = 50;

        /// <summary>
        /// Opens the main Transport info panel showing profitable opportunities
        /// </summary>
        public static void ShowStatsPanel(Player player, int maxItems = DefaultMaxItems)
        {
            EcoTransportModPlugin.DataService.RefreshAllData();
            var opportunities = EcoTransportModPlugin.DataService.GetAllOpportunities();

            var content = new LocStringBuilder();

            if (!opportunities.Any())
            {
                content.AppendLineLocStr("No profitable opportunities found.");
                content.AppendLineLocStr("No arbitrage opportunities available at this time.");
                player.OpenInfoPanel(
                    Localizer.DoStr("Transport - Market Data"),
                    content.ToLocString(),
                    "transport-stats");
                return;
            }

            // Header
            content.AppendLine();
            content.AppendLine(TextLoc.HeaderLocStr("Market Overview"));
            content.AppendLine();
            content.AppendLine(Localizer.Do($"Trade opportunities: {Text.Positive(opportunities.Count)}        Updated: {Text.Info(EcoTransportModPlugin.DataService.LastUpdate.ToString("HH:mm:ss"))}"));

            // Calculate total potential profit
            float totalPotentialProfit = opportunities.Sum(o => o.TotalProfit);
            content.AppendLine(Localizer.Do($"Total potential profit: {Text.Positive(Text.StyledNum(totalPotentialProfit))}"));
            content.AppendLine();

            // Group by product
            content.AppendLine(TextLoc.HeaderLocStr("Opportunities"));
            content.AppendLine();

            var groupedByProduct = opportunities.GroupBy(o => o.BuyFrom.ProductId).ToList();
            int displayedProducts = 0;

            foreach (var productGroup in groupedByProduct)
            {
                if (displayedProducts >= maxItems) break;

                AppendProductGroup(content, productGroup.ToList());
                content.AppendLine(); // Empty line between products
                displayedProducts++;
            }

            if (groupedByProduct.Count > maxItems)
            {
                content.AppendLine(Localizer.Do($"... and {Text.Info((groupedByProduct.Count - maxItems).ToString())} more products."));
                content.AppendLineLocStr("Use /transport panel <number> to show more.");
            }

            player.OpenInfoPanel(
                Localizer.DoStr("Transport - Profitable Opportunities"),
                content.ToLocString(),
                "transport-stats");
        }

        /// <summary>
        /// Appends a product group with all its opportunities
        /// </summary>
        private static void AppendProductGroup(LocStringBuilder content, List<TradeOpportunity> opportunities)
        {
            if (!opportunities.Any()) return;

            var first = opportunities.First();

            // Product header with item link
            content.AppendLine(Localizer.Do($"{first.GetItemLink()}"));

            // Each opportunity on its own line
            foreach (var opp in opportunities.OrderByDescending(o => o.TotalProfit))
            {
                var marginColor = opp.ProfitPercent >= 50 ? "#00FF00" : (opp.ProfitPercent >= 20 ? "#90EE90" : "#ADFF2F");
                content.AppendLine(Localizer.Do($"    - {opp.BuyFrom.GetStoreLink()}  →  {opp.SellTo.GetStoreLink()}"));
                content.AppendLine(Localizer.Do($"      Buy: {Text.Negative(Text.StyledNum(opp.BuyFrom.Price))} x {opp.BuyFrom.Quantity}           Sell: {Text.Positive(Text.StyledNum(opp.SellTo.Price))} x {opp.SellTo.Quantity}"));
                content.AppendLine(Localizer.Do($"      Margin: {Text.Color(marginColor, Text.StyledNum(opp.Margin))}              Qty: {opp.MaxQuantity}        Profit: {Text.Positive(Text.StyledNum(opp.TotalProfit))}       Distance: {opp.Distance:F0}meters"));
                content.AppendLine();
            }
        }

        /// <summary>
        /// Opens a search results panel
        /// </summary>
        public static void ShowSearchPanel(Player player, string searchTerm, int maxItems = DefaultMaxItems)
        {
            EcoTransportModPlugin.DataService.RefreshAllData();
            var results = EcoTransportModPlugin.DataService.SearchOpportunities(searchTerm);

            var content = new LocStringBuilder();

            if (!results.Any())
            {
                content.AppendLine(Localizer.Do($"No opportunities found matching '{Text.Info(searchTerm)}'."));
                content.AppendLineLocStr("Try a different search term or check /transport panel for all opportunities.");
                player.OpenInfoPanel(
                    Localizer.Do($"Transport - Search: {searchTerm}"),
                    content.ToLocString(),
                    "transport-search");
                return;
            }

            content.AppendLine();
            content.AppendLine(TextLoc.HeaderLocStr($"Search Results"));
            content.AppendLine();
            content.AppendLine(Localizer.Do($"Found {Text.Positive(results.Count.ToString())} opportunities matching '{Text.Info(searchTerm)}'"));
            content.AppendLine();

            var groupedByProduct = results.GroupBy(o => o.BuyFrom.ProductId).ToList();
            int displayedProducts = 0;

            foreach (var productGroup in groupedByProduct)
            {
                if (displayedProducts >= maxItems) break;

                AppendProductGroup(content, productGroup.ToList());
                content.AppendLine();
                displayedProducts++;
            }

            if (groupedByProduct.Count > maxItems)
            {
                content.AppendLine(Localizer.Do($"... and {Text.Info((groupedByProduct.Count - maxItems).ToString())} more products."));
            }

            player.OpenInfoPanel(
                Localizer.Do($"Transport - Search: {searchTerm}"),
                content.ToLocString(),
                "transport-search");
        }

        /// <summary>
        /// Shows detailed info for a specific trade opportunity
        /// </summary>
        public static void ShowOpportunityDetail(Player player, TradeOpportunity opp)
        {
            var content = new LocStringBuilder();
            var marginColor = opp.ProfitPercent >= 50 ? "#00FF00" : (opp.ProfitPercent >= 20 ? "#90EE90" : "#ADFF2F");

            // Item header
            content.AppendLine(TextLoc.HeaderLocStr("Trade Opportunity"));
            content.AppendLine();
            content.AppendLine(Localizer.Do($"Item: {opp.GetItemLink()}"));
            content.AppendLine(Localizer.Do($"Distance: {Text.Info($"{opp.Distance:F0}")} meters"));
            content.AppendLine();

            // Buy details
            content.AppendLine(TextLoc.HeaderLocStr("Buy From"));
            content.AppendLine(Localizer.Do($"Store: {opp.BuyFrom.GetStoreLink()}"));
            content.AppendLine(Localizer.Do($"Owner: {Text.Info(opp.BuyFrom.OwnerName)}"));
            content.AppendLine(Localizer.Do($"Price: {Text.Negative(Text.StyledNum(opp.BuyFrom.Price))}"));
            content.AppendLine(Localizer.Do($"Available: {Text.Info(opp.BuyFrom.Quantity.ToString())}"));
            content.AppendLine();

            // Sell details
            content.AppendLine(TextLoc.HeaderLocStr("Sell To"));
            content.AppendLine(Localizer.Do($"Store: {opp.SellTo.GetStoreLink()}"));
            content.AppendLine(Localizer.Do($"Owner: {Text.Info(opp.SellTo.OwnerName)}"));
            content.AppendLine(Localizer.Do($"Price: {Text.Positive(Text.StyledNum(opp.SellTo.Price))}"));
            content.AppendLine(Localizer.Do($"Wants: {Text.Info(opp.SellTo.Quantity.ToString())}"));
            content.AppendLine();

            // Profit analysis
            content.AppendLine(TextLoc.HeaderLocStr("Profit Analysis"));
            content.AppendLine(Localizer.Do($"Margin per unit: {Text.Color(marginColor, Text.StyledNum(opp.Margin))}"));
            content.AppendLine(Localizer.Do($"Profit percentage: {Text.Color(marginColor, Text.StyledPercent(opp.ProfitPercent / 100f))}"));
            content.AppendLine(Localizer.Do($"Max tradeable: {Text.Info(opp.MaxQuantity.ToString())} units"));
            content.AppendLine(Localizer.Do($"Distance: {Text.Info($"{opp.Distance:F0}")} meters"));
            content.AppendLine(Localizer.Do($"{Text.Bold("Total profit")}: {Text.Positive(Text.StyledNum(opp.TotalProfit))}"));

            player.OpenInfoPanel(
                Localizer.Do($"Transport - {opp.ProductName}"),
                content.ToLocString(),
                "transport-detail");
        }
    }

    [ChatCommandHandler]
    public static class TransportCommands
    {
        [ChatCommand("Shows commands for market/economy data.", ChatAuthorizationLevel.User)]
        public static void Transport(User user) { }

        [ChatSubCommand("Transport", "Shows global market statistics", "stats", ChatAuthorizationLevel.User)]
        public static void Stats(User user)
        {
            EcoTransportModPlugin.DataService.RefreshAllData();
            var opportunities = EcoTransportModPlugin.DataService.GetAllOpportunities();

            if (!opportunities.Any())
            {
                user.Player?.MsgLocStr("No profitable opportunities found.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(" ");
            sb.AppendLine("╔══════════════════════════════════════════════════════════════");
            sb.AppendLine("║       TRANSPORT - Trade Opportunities");
            sb.AppendLine($"║  Opportunities: {opportunities.Count,-5}  |  Updated: {EcoTransportModPlugin.DataService.LastUpdate:HH:mm:ss}");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════");

            foreach (var opp in opportunities.Take(10))
            {
                sb.AppendLine($"║  ► {opp.ProductName}");
                sb.AppendLine($"║    Buy:  {opp.BuyFrom.Price,8:F2} x{opp.BuyFrom.Quantity,-4} @ {opp.BuyFrom.StoreName}");
                sb.AppendLine($"║    Sell: {opp.SellTo.Price,8:F2} x{opp.SellTo.Quantity,-4} @ {opp.SellTo.StoreName}");
                sb.AppendLine($"║    Margin: {opp.Margin,8:F2} | Qty: {opp.MaxQuantity} | Profit: {opp.TotalProfit:F2}");
                sb.AppendLine("║");
            }

            if (opportunities.Count > 10)
                sb.AppendLine($"║  ... and {opportunities.Count - 10} more opportunities.");

            sb.AppendLine("╚══════════════════════════════════════════════════════════════");

            user.Player?.MsgLocStr(sb.ToString());
        }

        [ChatSubCommand("Transport", "Search for a specific product", "search", ChatAuthorizationLevel.User)]
        public static void Search(User user, string productName = "")
        {
            if (string.IsNullOrWhiteSpace(productName))
            {
                user.Player?.MsgLocStr("Usage: /transport search <product>");
                return;
            }

            EcoTransportModPlugin.DataService.RefreshAllData();
            var results = EcoTransportModPlugin.DataService.SearchOpportunities(productName);

            if (!results.Any())
            {
                user.Player?.MsgLocStr($"No opportunities found matching '{productName}'.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(" ");
            sb.AppendLine("╔══════════════════════════════════════════════════════════════");
            sb.AppendLine($"║  Search Results for: {productName}");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════");

            foreach (var opp in results.Take(10))
            {
                sb.AppendLine($"║  ► {opp.ProductName}");
                sb.AppendLine($"║    Buy:  {opp.BuyFrom.Price,8:F2} x{opp.BuyFrom.Quantity,-4} @ {opp.BuyFrom.StoreName}");
                sb.AppendLine($"║    Sell: {opp.SellTo.Price,8:F2} x{opp.SellTo.Quantity,-4} @ {opp.SellTo.StoreName}");
                sb.AppendLine($"║    Margin: {opp.Margin,8:F2} | Qty: {opp.MaxQuantity} | Profit: {opp.TotalProfit:F2}");
                sb.AppendLine("║");
            }

            if (results.Count > 10)
                sb.AppendLine($"║  ... and {results.Count - 10} more results.");

            sb.AppendLine("╚══════════════════════════════════════════════════════════════");

            user.Player?.MsgLocStr(sb.ToString());
        }

        [ChatSubCommand("Transport", "Export market data to JSON file", "export", ChatAuthorizationLevel.Admin)]
        public static void Export(User user)
        {
            try
            {
                EcoTransportModPlugin.DataService.RefreshAllData();
                var json = EcoTransportModPlugin.DataService.ExportToJson();

                var filename = $"economy_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
                var filepath = Path.Combine("Mods", "UserCode", "EcoTransportMod", filename);

                var directory = Path.GetDirectoryName(filepath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(filepath, json);

                user.Player?.MsgLocStr($"Market data exported to: {filepath}");
            }
            catch (Exception ex)
            {
                user.Player?.MsgLocStr($"Error exporting data: {ex.Message}");
            }
        }

        [ChatSubCommand("Transport", "Refresh market data cache", "refresh", ChatAuthorizationLevel.User)]
        public static void Refresh(User user)
        {
            EcoTransportModPlugin.DataService.RefreshAllData();
            var opportunities = EcoTransportModPlugin.DataService.GetAllOpportunities();

            user.Player?.MsgLocStr($"Market data refreshed. {opportunities.Count} trade opportunities found.");
        }

        [ChatSubCommand("Transport", "Show help for transport commands", "info", ChatAuthorizationLevel.User)]
        public static void Info(User user)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Transport Commands Help ===");
            sb.AppendLine("");
            sb.AppendLine("UI Panel Commands:");
            sb.AppendLine("  /transport panel - Open the main UI panel");
            sb.AppendLine("  /transport panel <n> - Show n items (max 200)");
            sb.AppendLine("  /transport find <product> - Search with UI panel");
            sb.AppendLine("  /transport detail <product> - Detailed product analysis");
            sb.AppendLine("");
            sb.AppendLine("Chat Commands:");
            sb.AppendLine("  /transport stats - Show global market statistics");
            sb.AppendLine("  /transport search <product> - Search for a specific product");
            sb.AppendLine("  /transport refresh - Refresh market data");
            sb.AppendLine("  /transport export - Export data to JSON (Admin only)");
            sb.AppendLine("  /transport info - Show this help");

            user.Player?.MsgLocStr(sb.ToString());
        }

        // ═══════════════════════════════════════════════════════════════
        // Native UI Panel Commands
        // ═══════════════════════════════════════════════════════════════

        [ChatSubCommand("Transport", "Opens the market data panel with native UI", "panel", ChatAuthorizationLevel.User)]
        public static void Panel(User user, int maxItems = 50)
        {
            if (user.Player == null)
            {
                user.MsgLocStr("This command requires an active player.");
                return;
            }

            if (maxItems < 1) maxItems = 1;
            if (maxItems > 200) maxItems = 200;

            TransportUIBuilder.ShowStatsPanel(user.Player, maxItems);
        }

        [ChatSubCommand("Transport", "Search for products with native UI panel", "find", ChatAuthorizationLevel.User)]
        public static void Find(User user, string productName = "")
        {
            if (user.Player == null)
            {
                user.MsgLocStr("This command requires an active player.");
                return;
            }

            if (string.IsNullOrWhiteSpace(productName))
            {
                user.Player.MsgLocStr("Usage: /transport find <product name>");
                return;
            }

            TransportUIBuilder.ShowSearchPanel(user.Player, productName);
        }

        [ChatSubCommand("Transport", "Show detailed info for a specific product", "detail", ChatAuthorizationLevel.User)]
        public static void Detail(User user, string productName = "")
        {
            if (user.Player == null)
            {
                user.MsgLocStr("This command requires an active player.");
                return;
            }

            if (string.IsNullOrWhiteSpace(productName))
            {
                user.Player.MsgLocStr("Usage: /transport detail <product name>");
                return;
            }

            EcoTransportModPlugin.DataService.RefreshAllData();
            var results = EcoTransportModPlugin.DataService.SearchOpportunities(productName);

            if (!results.Any())
            {
                user.Player.MsgLocStr($"No opportunities found matching '{productName}'.");
                return;
            }

            // Show detail for the first match
            TransportUIBuilder.ShowOpportunityDetail(user.Player, results.First());
        }
    }
}
