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
    using Eco.Gameplay.Components.Storage;
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
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Aliases;

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
        public IAlias Owner { get; set; }
        public WorldObject Store { get; set; }
        public StoreComponent StoreComp { get; set; }
        public Currency Currency { get; set; }
        public string CurrencyName => Currency?.Name ?? "Unknown";

        /// <summary>
        /// Gets the store's balance in its currency.
        /// Returns the amount of currency the store has available for buying items.
        /// </summary>
        public float GetStoreBalance()
        {
            var bankAccount = StoreComp?.BankAccount;
            if (bankAccount == null || Currency == null) return float.MaxValue; // If we can't check, assume unlimited
            return bankAccount.GetCurrencyHoldingVal(Currency);
        }

        public LocString GetCurrencyLink()
        {
            return Currency?.UILink() ?? Localizer.DoStr(CurrencyName);
        }

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

        public LocString GetOwnerLink()
        {
            return Localizer.DoStr(OwnerName);
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

        /// <summary>
        /// Maximum quantity that can be traded, limited by:
        /// - Available quantity at buy store
        /// - Wanted quantity at sell store
        /// - Sell store's available balance to pay for items
        /// - Sell store's storage capacity
        /// </summary>
        public int MaxQuantity
        {
            get
            {
                // Base limit: min of available and wanted quantities
                int baseLimit = Math.Min(BuyFrom.Quantity, SellTo.Quantity);

                // Check sell store balance (they need to pay us)
                float sellStoreBalance = SellTo.GetStoreBalance();
                if (sellStoreBalance < float.MaxValue && SellTo.Price > 0)
                {
                    int affordableByStore = (int)(sellStoreBalance / SellTo.Price);
                    baseLimit = Math.Min(baseLimit, affordableByStore);
                }

                // Check sell store storage capacity
                if (!CanSellToStoreAcceptItems())
                {
                    return 0; // Store storage is full, cannot accept any items
                }

                return Math.Max(0, baseLimit);
            }
        }

        /// <summary>
        /// Checks if the sell-to store has storage capacity to accept items
        /// </summary>
        private bool CanSellToStoreAcceptItems()
        {
            var (isFull, _, _, _, hasStorageLimit) = GetSellToStorageInfo();

            // If no storage limit detected, assume can accept
            if (!hasStorageLimit) return true;

            // Can accept if not full
            return !isFull;
        }

        /// <summary>
        /// Gets the storage status of the sell-to store
        /// Returns: (isFull, availableCapacity, totalSlots, usedSlots, hasStorageLimit)
        /// availableCapacity is the number of items that can be stored based on stack size
        /// </summary>
        public (bool isFull, int availableCapacity, int totalSlots, int usedSlots, bool hasStorageLimit) GetSellToStorageInfo()
        {
            if (SellTo?.Store == null || SellTo.ItemType == null)
                return (false, int.MaxValue, 0, 0, false);

            // Try to get LinkComponent to access linked storage
            var linkComponent = SellTo.Store.GetComponent<LinkComponent>();
            if (linkComponent == null)
                return (false, int.MaxValue, 0, 0, false); // No link component

            // Get linked inventories
            var linkedInventories = linkComponent.GetSortedLinkedInventories(SellTo.Store.Owners);
            if (linkedInventories == null)
                return (false, int.MaxValue, 0, 0, false); // No linked inventories

            // Get storage info from the linked inventories
            int totalSlots = linkedInventories.Stacks.Count();
            int usedSlots = linkedInventories.NonEmptyStacks.Count();
            int availableSlots = totalSlots - usedSlots;
            bool isFull = linkedInventories.IsFull;

            // If no valid storage was found
            if (totalSlots == 0)
                return (false, int.MaxValue, 0, 0, false);

            // Calculate available capacity based on item stack size
            var item = Item.Get(SellTo.ItemType);
            int maxStackSize = item?.MaxStackSize ?? 1;

            // Calculate capacity considering:
            // 1. Empty slots can hold maxStackSize items each
            // 2. Partially filled stacks of the same item can hold more
            int availableCapacity = availableSlots * maxStackSize;

            // Check if there are existing stacks of this item that aren't full
            foreach (var stack in linkedInventories.Stacks)
            {
                if (stack?.Item?.Type == SellTo.ItemType && stack.Quantity < maxStackSize)
                {
                    availableCapacity += (maxStackSize - stack.Quantity);
                }
            }

            return (isFull, availableCapacity, totalSlots, usedSlots, true);
        }

        public float TotalCost => BuyFrom.Price * MaxQuantity;
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
                var owner = worldObject.Owners;
                var ownerName = owner?.Name ?? "Unknown";
                var currency = store.Currency;

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
                        Owner = owner,
                        Store = worldObject,
                        StoreComp = store,
                        Currency = currency
                    });
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Gets all profitable trade opportunities (all combinations where sell price > buy price)
        /// Excludes opportunities where the requesting user owns the sell-to store
        /// </summary>
        public List<TradeOpportunity> GetAllOpportunities(User requestingUser)
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

                    // Find all profitable combinations with same currency
                    foreach (var buyFrom in sellOffers)
                    {
                        foreach (var sellTo in buyOffers)
                        {
                            // Only match if same currency and profitable
                            if (buyFrom.Currency == sellTo.Currency && sellTo.Price > buyFrom.Price)
                            {
                                // Skip if requesting user owns the sell-to store
                                if (sellTo.Owner != null && sellTo.Owner.ContainsUser(requestingUser))
                                    continue;

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

        public List<TradeOpportunity> SearchOpportunities(string searchTerm, User requestingUser)
        {
            var allData = GetAllOpportunities(requestingUser);
            if (string.IsNullOrWhiteSpace(searchTerm))
                return allData;

            var term = searchTerm.ToLower();
            return allData
                .Where(o => o.ProductName.ToLower().Contains(term) ||
                           o.BuyFrom.StoreName.ToLower().Contains(term) ||
                           o.SellTo.StoreName.ToLower().Contains(term))
                .ToList();
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
        public static void ShowStatsPanel(Player player, User user, int maxItems = DefaultMaxItems)
        {
            EcoTransportModPlugin.DataService.RefreshAllData();
            var opportunities = EcoTransportModPlugin.DataService.GetAllOpportunities(user);

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

                AppendProductGroup(content, productGroup.ToList(), user);
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
        /// Gets the user's balance for a specific currency from their bank account
        /// </summary>
        private static float GetUserBalance(User user, Currency currency)
        {
            if (user?.BankAccount == null || currency == null) return 0f;
            return user.BankAccount.GetCurrencyHoldingVal(currency);
        }

        /// <summary>
        /// Appends a product group with all its opportunities
        /// </summary>
        private static void AppendProductGroup(LocStringBuilder content, List<TradeOpportunity> opportunities, User user)
        {
            if (!opportunities.Any()) return;

            var first = opportunities.First();

            // Product header with item link
            content.AppendLine(Localizer.Do($"{first.GetItemLink()}"));

            // Each opportunity on its own line
            foreach (var opp in opportunities.OrderByDescending(o => o.TotalProfit))
            {
                var marginColor = "#9CCD4F";
                var currencyLink = opp.BuyFrom.GetCurrencyLink();

                // Check if user can afford this opportunity
                float userBalance = GetUserBalance(user, opp.BuyFrom.Currency);
                bool canAfford = userBalance >= opp.TotalCost;
                var totalCostColor = canAfford ? "#9CCD4F" : "#FF6B6B"; // Green if affordable, light red if not

                // Get storage info for sell-to store
                var (isFull, availableCapacity, totalSlots, usedSlots, hasStorageLimit) = opp.GetSellToStorageInfo();

                content.AppendLine(Localizer.Do($"    - {opp.BuyFrom.GetStoreLink()}  →  {opp.SellTo.GetStoreLink()}"));

                // Build storage status message - always show if has storage limit
                string storageStatus = "";
                if (hasStorageLimit)
                {
                    if (isFull)
                    {
                        storageStatus = Text.Color("#FF6B6B", "FULL - Cannot accept items");
                    }
                    else if (availableCapacity < opp.MaxQuantity)
                    {
                        storageStatus = Text.Color("#FFA500", $"Limited ({availableCapacity} items can fit)");
                    }
                    else
                    {
                        storageStatus = Text.Positive($"✓");
                    }
                }
                var affordMsg = canAfford ? "" : $"  {Text.Color(totalCostColor, "(you only have " + Text.StyledNum(userBalance) + ")")}";
                content.AppendLine(Localizer.Do($"      Buy x {Text.Info($"{opp.MaxQuantity}")} at {Text.Positive(Text.Bold(Text.StyledNum(opp.BuyFrom.Price)))} {currencyLink}            →            Sell at {Text.Positive(Text.Bold(Text.StyledNum(opp.SellTo.Price)))} {currencyLink}"));
                content.AppendLine(Localizer.Do($"      Total Investment:   {Text.Color(totalCostColor, Text.Bold(opp.TotalCost))} {currencyLink} {affordMsg} "));
                if (hasStorageLimit)
                {
                    content.AppendLine(Localizer.Do($"      Storage capacity: {Text.Bold(storageStatus)}"));
                }
                content.AppendLine(Localizer.Do($"      Distance:   {Text.Info($"{opp.Distance:F0}")} meters"));
                content.AppendLine(Localizer.Do($"      Margin:  {Text.Bold(Text.Color(marginColor, Text.StyledNum(opp.Margin)))}    Profit: {Text.Positive(Text.Bold(Text.StyledNum(opp.TotalProfit)))} {currencyLink}"));
                content.AppendLine();
            }
        }

        /// <summary>
        /// Opens a search results panel
        /// </summary>
        public static void ShowSearchPanel(Player player, User user, string searchTerm, int maxItems = DefaultMaxItems)
        {
            EcoTransportModPlugin.DataService.RefreshAllData();
            var results = EcoTransportModPlugin.DataService.SearchOpportunities(searchTerm, user);

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

                AppendProductGroup(content, productGroup.ToList(), user);
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
        public static void ShowOpportunityDetail(Player player, User user, TradeOpportunity opp)
        {
            var content = new LocStringBuilder();
            var marginColor = "#9CCD4F";

            // Check if user can afford this opportunity
            float userBalance = GetUserBalance(user, opp.BuyFrom.Currency);
            bool canAfford = userBalance >= opp.TotalCost;
            var totalCostColor = canAfford ? "#9CCD4F" : "#FF6B6B"; // Green if affordable, light red if not

            // Item header
            content.AppendLine(TextLoc.HeaderLocStr("Trade Opportunity"));
            content.AppendLine();
            content.AppendLine(Localizer.Do($"Item: {opp.GetItemLink()}"));
            content.AppendLine(Localizer.Do($"Distance: {Text.Info($"{opp.Distance:F0}")} meters"));
            content.AppendLine();

            var currencyLink = opp.BuyFrom.GetCurrencyLink();

            // Buy details
            content.AppendLine(TextLoc.HeaderLocStr("Buy From"));
            content.AppendLine(Localizer.Do($"Store: {opp.BuyFrom.GetStoreLink()}"));
            content.AppendLine(Localizer.Do($"Owner: {opp.BuyFrom.GetOwnerLink()}"));
            content.AppendLine(Localizer.Do($"Price: {Text.Negative(Text.Bold(Text.StyledNum(opp.BuyFrom.Price)))} {currencyLink}"));
            content.AppendLine(Localizer.Do($"Available: {Text.Info(opp.BuyFrom.Quantity.ToString())}"));
            content.AppendLine();

            // Sell details
            content.AppendLine(TextLoc.HeaderLocStr("Sell To"));
            content.AppendLine(Localizer.Do($"Store: {opp.SellTo.GetStoreLink()}"));
            content.AppendLine(Localizer.Do($"Owner: {opp.SellTo.GetOwnerLink()}"));
            content.AppendLine(Localizer.Do($"Price: {Text.Positive(Text.Bold(Text.StyledNum(opp.SellTo.Price)))} {currencyLink}"));
            content.AppendLine(Localizer.Do($"Wants: {Text.Info(opp.SellTo.Quantity.ToString())}"));

            // Storage info
            var (isFull, availableCapacity, totalSlots, usedSlots, hasStorageLimit) = opp.GetSellToStorageInfo();
            if (hasStorageLimit)
            {
                if (isFull)
                {
                    content.AppendLine(Localizer.Do($"Storage: {Text.Color("#FF6B6B", "FULL - Cannot accept items")}"));
                }
                else if (availableCapacity < opp.MaxQuantity)
                {
                    content.AppendLine(Localizer.Do($"Storage: {Text.Color("#FFA500", $"Limited ({availableCapacity} items can fit)")}"));
                }
                else
                {
                    content.AppendLine(Localizer.Do($"Storage: {Text.Positive($"{availableCapacity} units can fit")}"));
                }
            }
            else
            {
                content.AppendLine(Localizer.Do($"Storage: {Text.Info("Unlimited")}"));
            }
            content.AppendLine();

            // Profit analysis
            content.AppendLine(TextLoc.HeaderLocStr("Profit Analysis"));
            content.AppendLine(Localizer.Do($"Currency: {currencyLink}"));
            content.AppendLine(Localizer.Do($"Margin per unit: {Text.Bold(Text.Color(marginColor, Text.StyledNum(opp.Margin)))} {currencyLink}"));
            content.AppendLine(Localizer.Do($"Profit percentage: {Text.Bold(Text.Color(marginColor, Text.StyledPercent(opp.ProfitPercent / 100f)))}"));
            content.AppendLine(Localizer.Do($"Max tradeable: {Text.Info(opp.MaxQuantity.ToString())} units"));
            content.AppendLine(Localizer.Do($"Distance: {Text.Info($"{opp.Distance:F0}")} meters"));
            content.AppendLine(Localizer.Do($"{Text.Bold("Total investment")}: {Text.Color(totalCostColor, Text.Bold(opp.TotalCost))} {currencyLink}"));
            content.AppendLine(Localizer.Do($"{Text.Bold("Total profit")}: {Text.Positive(Text.Bold(Text.StyledNum(opp.TotalProfit)))} {currencyLink}"));

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

        [ChatSubCommand("Transport", "Refresh market data cache", "refresh", ChatAuthorizationLevel.User)]
        public static void Refresh(User user)
        {
            EcoTransportModPlugin.DataService.RefreshAllData();
            var opportunities = EcoTransportModPlugin.DataService.GetAllOpportunities(user);

            user.Player?.MsgLocStr($"Market data refreshed. {opportunities.Count} trade opportunities found.");
        }

        [ChatSubCommand("Transport", "Show help for transport commands", "info", ChatAuthorizationLevel.User)]
        public static void Info(User user)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Transport Commands Help ===");
            sb.AppendLine("");
            sb.AppendLine("  /transport panel - Open the main UI panel");
            sb.AppendLine("  /transport panel <n> - Show n items (max 200)");
            sb.AppendLine("  /transport find <product> - Search with UI panel");
            sb.AppendLine("  /transport detail <product> - Detailed product analysis");
            sb.AppendLine("  /transport refresh - Refresh market data");
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

            TransportUIBuilder.ShowStatsPanel(user.Player, user, maxItems);
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

            TransportUIBuilder.ShowSearchPanel(user.Player, user, productName);
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
            var results = EcoTransportModPlugin.DataService.SearchOpportunities(productName, user);

            if (!results.Any())
            {
                user.Player.MsgLocStr($"No opportunities found matching '{productName}'.");
                return;
            }

            // Show detail for the first match
            TransportUIBuilder.ShowOpportunityDetail(user.Player, user, results.First());
        }
    }
}
