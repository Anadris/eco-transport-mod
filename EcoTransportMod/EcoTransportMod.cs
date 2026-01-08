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
    using Eco.Mods.TechTree;

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

    /// <summary>
    /// Main plugin class for EcoTransportMod
    /// </summary>
    public class EcoTransportModPlugin : IModKitPlugin, IInitializablePlugin, IShutdownablePlugin
    {
        public static EcoTransportModPlugin Instance { get; private set; }
        public static EconomyDataService DataService { get; private set; }
        public static UsageStatsService StatsService { get; private set; }

        public string GetCategory() => Localizer.DoStr("Economy");
        public string GetStatus() => Localizer.DoStr("Running");
        public override string ToString() => Localizer.DoStr("Eco Transport Mod");

        public void Initialize(TimedTask timer)
        {
            Instance = this;
            DataService = new EconomyDataService();
            DataService.Initialize();
            StatsService = new UsageStatsService();
        }

        public System.Threading.Tasks.Task ShutdownAsync()
        {
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Represents a single usage record
    /// </summary>
    public class UsageRecord
    {
        public string PlayerName { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Service for tracking command usage statistics
    /// </summary>
    public class UsageStatsService
    {
        private readonly Dictionary<string, List<UsageRecord>> _usageByCommand = new Dictionary<string, List<UsageRecord>>();
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        /// <summary>
        /// Records a command usage by a player
        /// </summary>
        public void RecordUsage(User user, string commandName)
        {
            if (user == null || string.IsNullOrEmpty(commandName)) return;

            _lock.EnterWriteLock();
            try
            {
                if (!_usageByCommand.ContainsKey(commandName))
                {
                    _usageByCommand[commandName] = new List<UsageRecord>();
                }

                _usageByCommand[commandName].Add(new UsageRecord
                {
                    PlayerName = user.Name,
                    Timestamp = DateTime.UtcNow
                });
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Gets usage statistics for a specific command sorted by count (descending) (internal, no lock)
        /// </summary>
        private List<(string PlayerName, int Count)> GetCommandStatsInternal(string commandName, TimeSpan? timeWindow = null)
        {
            if (!_usageByCommand.ContainsKey(commandName))
                return new List<(string, int)>();

            var records = _usageByCommand[commandName];

            // Filter by time window if specified
            if (timeWindow.HasValue)
            {
                var cutoffTime = DateTime.UtcNow - timeWindow.Value;
                records = records.Where(r => r.Timestamp >= cutoffTime).ToList();
            }

            return records
                .GroupBy(r => r.PlayerName)
                .Select(g => (g.Key, g.Count()))
                .OrderByDescending(x => x.Item2)
                .ToList();
        }

        /// <summary>
        /// Gets usage statistics for a specific command sorted by count (descending)
        /// </summary>
        public List<(string PlayerName, int Count)> GetCommandStats(string commandName, TimeSpan? timeWindow = null)
        {
            _lock.EnterReadLock();
            try
            {
                return GetCommandStatsInternal(commandName, timeWindow);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Gets all usage statistics grouped by command
        /// </summary>
        public Dictionary<string, List<(string PlayerName, int Count)>> GetAllStats(TimeSpan? timeWindow = null)
        {
            _lock.EnterReadLock();
            try
            {
                var result = new Dictionary<string, List<(string, int)>>();
                foreach (var command in _usageByCommand.Keys)
                {
                    var stats = GetCommandStatsInternal(command, timeWindow);
                    if (stats.Any())
                    {
                        result[command] = stats;
                    }
                }
                return result;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Gets total usage count for all commands
        /// </summary>
        public int GetTotalUsageCount(TimeSpan? timeWindow = null)
        {
            _lock.EnterReadLock();
            try
            {
                if (timeWindow.HasValue)
                {
                    var cutoffTime = DateTime.UtcNow - timeWindow.Value;
                    return _usageByCommand.Values.Sum(list => list.Count(r => r.Timestamp >= cutoffTime));
                }
                return _usageByCommand.Values.Sum(list => list.Count);
            }
            finally
            {
                _lock.ExitReadLock();
            }
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
    /// Represents a stop in a delivery route
    /// </summary>
    public class RouteStop
    {
        public TradeOpportunity Opportunity { get; set; }
        public int StopNumber { get; set; }
        public float DistanceFromPreviousStop { get; set; }
        public float CumulativeDistance { get; set; }
        public float CumulativeProfit { get; set; }
    }

    /// <summary>
    /// Represents a complete delivery route with multiple stops
    /// </summary>
    public class DeliveryRoute
    {
        public List<RouteStop> Stops { get; set; } = new List<RouteStop>();
        public float TotalDistance { get; set; }
        public float TotalProfit { get; set; }
        public float TotalCost { get; set; }
        public float EfficiencyRatio => TotalDistance > 0 ? TotalProfit / TotalDistance : 0;

        public int StopCount => Stops.Count;

        /// <summary>
        /// Get the starting store (first buy location)
        /// </summary>
        public WorldObject StartStore => Stops.FirstOrDefault()?.Opportunity.BuyFrom.Store;

        /// <summary>
        /// Get the ending store (last sell location)
        /// </summary>
        public WorldObject EndStore => Stops.LastOrDefault()?.Opportunity.SellTo.Store;
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

        /// <summary>
        /// Finds optimized delivery routes by chaining opportunities where sell location becomes next buy location
        /// </summary>
        public List<DeliveryRoute> FindOptimizedRoutes(User requestingUser, int maxResults = 10)
        {
            var allOpportunities = GetAllOpportunities(requestingUser);
            var routes = new List<DeliveryRoute>();
            var visited = new HashSet<string>();

            // Start from each opportunity and try to build chains
            foreach (var startOpp in allOpportunities)
            {
                var route = new DeliveryRoute();
                var currentOpps = new List<TradeOpportunity> { startOpp };
                var routeKey = $"{startOpp.BuyFrom.Store.ID}_{startOpp.SellTo.Store.ID}_{startOpp.BuyFrom.ProductId}";

                if (visited.Contains(routeKey))
                    continue;

                BuildRouteRecursive(startOpp, allOpportunities, route, new HashSet<WorldObject>(), visited);

                if (route.Stops.Count > 0)
                {
                    routes.Add(route);
                }
            }

            // Sort by total profit descending, then by efficiency ratio, then by number of stops
            return routes
                .OrderByDescending(r => r.TotalProfit)
                .ThenByDescending(r => r.EfficiencyRatio)
                .ThenByDescending(r => r.StopCount)
                .Take(maxResults)
                .ToList();
        }

        /// <summary>
        /// Recursively builds a route by chaining compatible opportunities
        /// </summary>
        private void BuildRouteRecursive(
            TradeOpportunity currentOpp,
            List<TradeOpportunity> allOpportunities,
            DeliveryRoute route,
            HashSet<WorldObject> visitedStores,
            HashSet<string> globalVisited)
        {
            // Add current opportunity as a stop
            var stopNumber = route.Stops.Count + 1;
            var prevStop = route.Stops.LastOrDefault();

            float distanceFromPrevious = 0f;
            if (prevStop != null)
            {
                // Distance from previous sell location to current buy location
                var pos1 = prevStop.Opportunity.SellTo.Store.Position3i;
                var pos2 = currentOpp.BuyFrom.Store.Position3i;
                distanceFromPrevious = (float)Math.Sqrt(
                    Math.Pow(pos2.X - pos1.X, 2) +
                    Math.Pow(pos2.Y - pos1.Y, 2) +
                    Math.Pow(pos2.Z - pos1.Z, 2));
            }

            var newStop = new RouteStop
            {
                Opportunity = currentOpp,
                StopNumber = stopNumber,
                DistanceFromPreviousStop = distanceFromPrevious,
                CumulativeDistance = (prevStop?.CumulativeDistance ?? 0) + currentOpp.Distance,
                CumulativeProfit = (prevStop?.CumulativeProfit ?? 0) + currentOpp.TotalProfit
            };

            route.Stops.Add(newStop);
            route.TotalDistance = newStop.CumulativeDistance;
            route.TotalProfit = newStop.CumulativeProfit;
            route.TotalCost += currentOpp.TotalCost;

            // Mark stores as visited to prevent loops
            visitedStores.Add(currentOpp.BuyFrom.Store);
            visitedStores.Add(currentOpp.SellTo.Store);

            // Find next opportunities where current sell location matches next buy location
            var nextOpportunities = allOpportunities
                .Where(opp =>
                    opp.BuyFrom.Store == currentOpp.SellTo.Store && // Chain: sell here, buy from here
                    !visitedStores.Contains(opp.SellTo.Store) && // Don't revisit stores
                    opp.BuyFrom.Currency == currentOpp.SellTo.Currency) // Same currency
                .OrderByDescending(opp => opp.TotalProfit)
                .ToList();

            // If we found compatible next stops, continue with the best one
            if (nextOpportunities.Any())
            {
                var nextOpp = nextOpportunities.First();
                BuildRouteRecursive(nextOpp, allOpportunities, route, visitedStores, globalVisited);
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

            // Calculate total potential profit grouped by currency
            var profitByCurrency = opportunities
                .GroupBy(o => o.BuyFrom.Currency)
                .Select(g => new
                {
                    Currency = g.Key,
                    TotalProfit = g.Sum(o => o.TotalProfit)
                })
                .OrderByDescending(x => x.TotalProfit);

            content.Append(Localizer.DoStr("Total potential profit: "));
            bool first = true;
            foreach (var profitGroup in profitByCurrency)
            {
                if (!first) content.Append(Localizer.DoStr("  |  "));
                var currencyLink = profitGroup.Currency != null ? profitGroup.Currency.UILink() : Text.Info("Unknown");
                content.Append(Localizer.Do($"{Text.Positive(Text.StyledNum(profitGroup.TotalProfit))} {currencyLink}"));
                first = false;
            }
            content.AppendLine();
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

        /// <summary>
        /// Opens the routes panel showing optimized multi-stop delivery routes
        /// </summary>
        public static void ShowRoutesPanel(Player player, User user, List<DeliveryRoute> routes)
        {
            var content = new LocStringBuilder();

            // Header
            content.AppendLine();
            content.AppendLine(TextLoc.HeaderLocStr("Optimized Delivery Routes"));
            content.AppendLine();
            content.AppendLine(Localizer.Do($"Found {Text.Positive(routes.Count.ToString())} multi-stop routes        Updated: {Text.Info(EcoTransportModPlugin.DataService.LastUpdate.ToString("HH:mm:ss"))}"));

            // Calculate total potential profit from all routes grouped by currency
            var profitByCurrency = routes
                .SelectMany(r => r.Stops)
                .GroupBy(s => s.Opportunity.BuyFrom.Currency)
                .Select(g => new
                {
                    Currency = g.Key,
                    TotalProfit = g.Sum(s => s.Opportunity.TotalProfit)
                })
                .OrderByDescending(x => x.TotalProfit);

            content.Append(Localizer.DoStr("Total potential profit: "));
            bool first = true;
            foreach (var profitGroup in profitByCurrency)
            {
                if (!first) content.Append(Localizer.DoStr("  |  "));
                var currencyLink = profitGroup.Currency != null ? profitGroup.Currency.UILink() : Text.Info("Unknown");
                content.Append(Localizer.Do($"{Text.Positive(Text.StyledNum(profitGroup.TotalProfit))} {currencyLink}"));
                first = false;
            }
            content.AppendLine();
            content.AppendLine();

            int routeNumber = 1;
            foreach (var route in routes)
            {
                // Route header
                content.AppendLine(TextLoc.HeaderLocStr($"Route #{routeNumber}"));
                content.AppendLine();

                // Route summary
                var currencyLink = route.Stops.First().Opportunity.BuyFrom.GetCurrencyLink();
                var currency = route.Stops.First().Opportunity.BuyFrom.Currency;
                var startStoreLink = route.StartStore.UILink();
                var endStoreLink = route.EndStore.UILink();

                // Check if user can afford this route
                float userBalance = GetUserBalance(user, currency);
                bool canAfford = userBalance >= route.TotalCost;
                var totalCostColor = canAfford ? "#9CCD4F" : "#FF6B6B"; // Green if affordable, light red if not
                var affordMsg = canAfford ? "" : $"  {Text.Color(totalCostColor, "(you only have " + Text.StyledNum(userBalance) + ")")}";

                content.AppendLine(Localizer.Do($"Total Investment: {Text.Color(totalCostColor, Text.Bold(route.TotalCost.ToString()))} {currencyLink}{affordMsg}        Total Profit: {Text.Positive(Text.Bold(Text.StyledNum(route.TotalProfit)))} {currencyLink}"));
                content.AppendLine(Localizer.Do($"Stops: {Text.Info(route.StopCount.ToString())}        Distance: {Text.Info($"{route.TotalDistance:F0}")} meters        Efficiency: {Text.Info($"{route.EfficiencyRatio:F2}")} profit/meter"));
                content.AppendLine();

                // Individual stops
                foreach (var stop in route.Stops)
                {
                    var opp = stop.Opportunity;
                    content.AppendLine(Localizer.Do($"  {Text.Color(Color.Orange, Text.Bold($"Stop {stop.StopNumber}"))} - {opp.GetItemLink()}"));

                    content.AppendLine(Localizer.Do($"    {opp.BuyFrom.GetStoreLink()}  →  {opp.SellTo.GetStoreLink()}"));
                    content.AppendLine(Localizer.Do($"    Buy x{Text.Info(opp.MaxQuantity.ToString())} at {Text.Positive(Text.Bold(Text.StyledNum(opp.BuyFrom.Price)))} {currencyLink}  →  Sell at {Text.Positive(Text.Bold(Text.StyledNum(opp.SellTo.Price)))} {currencyLink}"));

                    // Storage info for sell-to store
                    var (isFull, availableCapacity, totalSlots, usedSlots, hasStorageLimit) = opp.GetSellToStorageInfo();
                    string storageStatus = "";
                    if (hasStorageLimit)
                    {
                        if (isFull)
                        {
                            storageStatus = $"    |    Storage: {Text.Color("#FF6B6B", "FULL")}";
                        }
                        else if (availableCapacity < opp.MaxQuantity)
                        {
                            storageStatus = $"    |    Storage: {Text.Color("#FFA500", $"Limited ({availableCapacity})")}";
                        }
                        else
                        {
                            storageStatus = $"    |    Storage: {Text.Positive("✓")}";
                        }
                    }

                    content.AppendLine(Localizer.Do($"    Distance: {Text.Info($"{opp.Distance:F0}")}m    |    Profit: {Text.Positive(Text.Bold(Text.StyledNum(opp.TotalProfit)))} {currencyLink}{storageStatus}"));
                    content.AppendLine();
                }

                content.AppendLine();
                routeNumber++;
            }

            player.OpenInfoPanel(
                Localizer.DoStr("Transport - Optimized Routes"),
                content.ToLocString(),
                "transport-routes");
        }
    }

    [ChatCommandHandler]
    public static class TransportCommands
    {
        /// <summary>
        /// Checks if the user has the Logistics skill level 1 required to use transport commands
        /// </summary>
        private static bool HasRequiredLogisticsSkill(User user)
        {
            // If skill requirement is disabled in config, allow all users
            if (!EcoTransportModConfig.REQUIRE_LOGISTICS_SKILL)
                return true;

            // Otherwise, check for Logistics skill level 1+
            if (user?.Skillset == null) return false;
            var logisticsSkill = user.Skillset.GetSkill(typeof(LogisticsSkill));
            return logisticsSkill != null && logisticsSkill.Level >= 1;
        }

        /// <summary>
        /// Shows an error message to users without the required skill
        /// </summary>
        private static void ShowSkillRequiredMessage(User user)
        {
            user.Player?.MsgLocStr($"{Text.Error("Transport commands require Logistics skill level 1.")}\nLearn the Logistics specialty and level it up to access market analysis features.");
        }

        [ChatCommand("Shows commands for market/economy data. Requires Logistics skill level 1.", ChatAuthorizationLevel.User)]
        public static void Transport(User user) { }

        [ChatSubCommand("Transport", "Refresh market data cache", "refresh", ChatAuthorizationLevel.User)]
        public static void Refresh(User user)
        {
            if (!HasRequiredLogisticsSkill(user))
            {
                ShowSkillRequiredMessage(user);
                return;
            }

            EcoTransportModPlugin.StatsService.RecordUsage(user, "refresh");
            EcoTransportModPlugin.DataService.RefreshAllData();
            var opportunities = EcoTransportModPlugin.DataService.GetAllOpportunities(user);

            user.Player?.MsgLocStr($"Market data refreshed. {opportunities.Count} trade opportunities found.");
        }

        [ChatSubCommand("Transport", "Show help for transport commands", "info", ChatAuthorizationLevel.User)]
        public static void Info(User user)
        {
            if (!HasRequiredLogisticsSkill(user))
            {
                ShowSkillRequiredMessage(user);
                return;
            }

            EcoTransportModPlugin.StatsService.RecordUsage(user, "info");
            var sb = new StringBuilder();
            sb.AppendLine("=== Transport Commands Help ===");
            sb.AppendLine("");
            sb.AppendLine("  /transport panel - Open the main UI panel");
            sb.AppendLine("  /transport panel <n> - Show n items (max 200)");
            sb.AppendLine("  /transport route [max] - Find optimized multi-stop routes");
            sb.AppendLine("  /transport find <product> - Search with UI panel");
            sb.AppendLine("  /transport detail <product> - Detailed product analysis");
            sb.AppendLine("  /transport refresh - Refresh market data");
            sb.AppendLine("  /transport stats - Show usage statistics");
            sb.AppendLine("  /transport info - Show this help");

            user.Player?.MsgLocStr(sb.ToString());
        }

        [ChatSubCommand("Transport", "Show command usage statistics by player", "stats", ChatAuthorizationLevel.User)]
        public static void Stats(User user)
        {
            if (!HasRequiredLogisticsSkill(user))
            {
                ShowSkillRequiredMessage(user);
                return;
            }

            EcoTransportModPlugin.StatsService.RecordUsage(user, "stats");

            if (user.Player == null)
            {
                user.MsgLocStr("This command requires an active player.");
                return;
            }

            var allStats = EcoTransportModPlugin.StatsService.GetAllStats();
            var last24hStats = EcoTransportModPlugin.StatsService.GetAllStats(TimeSpan.FromHours(24));

            var content = new LocStringBuilder();

            content.AppendLine();
            content.AppendLine(TextLoc.HeaderLocStr("Transport Command Usage Statistics"));
            content.AppendLine();

            if (!allStats.Any())
            {
                content.AppendLineLocStr("No usage data available yet.");
                content.AppendLineLocStr("Use /transport commands to start tracking.");
                user.Player.OpenInfoPanel(
                    Localizer.DoStr("Transport - Usage Stats"),
                    content.ToLocString(),
                    "transport-stats-panel");
                return;
            }

            int totalUsage = EcoTransportModPlugin.StatsService.GetTotalUsageCount();
            int last24hUsage = EcoTransportModPlugin.StatsService.GetTotalUsageCount(TimeSpan.FromHours(24));
            content.AppendLine(Localizer.Do($"Total commands (all time): {Text.Positive(totalUsage.ToString())}"));
            content.AppendLine(Localizer.Do($"Last 24 hours: {Text.Info(last24hUsage.ToString())}"));
            content.AppendLine();

            // Show stats for each command
            foreach (var commandStats in allStats.OrderByDescending(x => x.Value.Sum(s => s.Count)))
            {
                string commandName = commandStats.Key;
                var playerStats = commandStats.Value;
                int commandTotal = playerStats.Sum(s => s.Count);

                // Get 24h stats for this command
                int command24hTotal = 0;
                List<(string, int)> player24hStats = new List<(string, int)>();
                if (last24hStats.ContainsKey(commandName))
                {
                    player24hStats = last24hStats[commandName];
                    command24hTotal = player24hStats.Sum(s => s.Item2);
                }

                content.AppendLine(TextLoc.HeaderLocStr($"/{commandName}"));
                content.AppendLine(Localizer.Do($"All time: {Text.Positive(commandTotal.ToString())} uses  |  Last 24h: {Text.Info(command24hTotal.ToString())} uses  |  Unique players: {Text.Info(playerStats.Count.ToString())}"));
                content.AppendLine();

                // Show all-time top 10
                content.AppendLine(Localizer.Do($"{Text.Bold("All Time Top 10:")}"));
                int rank = 1;
                foreach (var (playerName, count) in playerStats.Take(10))
                {
                    var rankText = rank <= 3 ? Text.Bold($"#{rank}") : $"#{rank}";
                    var countText = Text.Positive(count.ToString());

                    // Get player object for tooltip
                    var player = UserManager.FindUserByName(playerName);
                    var playerLink = player != null ? player.UILink() : Localizer.DoStr(playerName);

                    content.AppendLine(Localizer.Do($"  {rankText}  {playerLink}: {countText} times"));
                    rank++;
                }

                if (playerStats.Count > 10)
                {
                    content.AppendLine(Localizer.Do($"  ... and {Text.Info((playerStats.Count - 10).ToString())} more players"));
                }
                content.AppendLine();

                // Show 24h stats if available
                if (player24hStats.Any())
                {
                    content.AppendLine(Localizer.Do($"{Text.Bold("Last 24 Hours Top 10:")}"));
                    rank = 1;
                    foreach (var (playerName, count) in player24hStats.Take(10))
                    {
                        var rankText = rank <= 3 ? Text.Bold($"#{rank}") : $"#{rank}";
                        var countText = Text.Info(count.ToString());

                        // Get player object for tooltip
                        var player = UserManager.FindUserByName(playerName);
                        var playerLink = player != null ? player.UILink() : Localizer.DoStr(playerName);

                        content.AppendLine(Localizer.Do($"  {rankText}  {playerLink}: {countText} times"));
                        rank++;
                    }

                    if (player24hStats.Count > 10)
                    {
                        content.AppendLine(Localizer.Do($"  ... and {Text.Info((player24hStats.Count - 10).ToString())} more players"));
                    }
                }
                else
                {
                    content.AppendLine(Localizer.Do($"{Text.Bold("Last 24 Hours:")} No usage"));
                }

                content.AppendLine();
            }

            user.Player.OpenInfoPanel(
                Localizer.DoStr("Transport - Usage Stats"),
                content.ToLocString(),
                "transport-stats-panel");
        }

        // ═══════════════════════════════════════════════════════════════
        // Native UI Panel Commands
        // ═══════════════════════════════════════════════════════════════

        [ChatSubCommand("Transport", "Opens the market data panel with native UI", "panel", ChatAuthorizationLevel.User)]
        public static void Panel(User user, int maxItems = 50)
        {
            if (!HasRequiredLogisticsSkill(user))
            {
                ShowSkillRequiredMessage(user);
                return;
            }

            if (user.Player == null)
            {
                user.MsgLocStr("This command requires an active player.");
                return;
            }

            if (maxItems < 1) maxItems = 1;
            if (maxItems > 200) maxItems = 200;

            EcoTransportModPlugin.StatsService.RecordUsage(user, "panel");
            TransportUIBuilder.ShowStatsPanel(user.Player, user, maxItems);
        }

        [ChatSubCommand("Transport", "Search for products with native UI panel", "find", ChatAuthorizationLevel.User)]
        public static void Find(User user, string productName = "")
        {
            if (!HasRequiredLogisticsSkill(user))
            {
                ShowSkillRequiredMessage(user);
                return;
            }

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

            EcoTransportModPlugin.StatsService.RecordUsage(user, "find");
            TransportUIBuilder.ShowSearchPanel(user.Player, user, productName);
        }

        [ChatSubCommand("Transport", "Find optimized multi-stop delivery routes", "route", ChatAuthorizationLevel.User)]
        public static void Route(User user, int maxRoutes = 10)
        {
            if (!HasRequiredLogisticsSkill(user))
            {
                ShowSkillRequiredMessage(user);
                return;
            }

            if (user.Player == null)
            {
                user.MsgLocStr("This command requires an active player.");
                return;
            }

            if (maxRoutes < 1) maxRoutes = 1;
            if (maxRoutes > 20) maxRoutes = 20;

            EcoTransportModPlugin.StatsService.RecordUsage(user, "route");
            EcoTransportModPlugin.DataService.RefreshAllData();

            var routes = EcoTransportModPlugin.DataService.FindOptimizedRoutes(user, maxRoutes);

            if (!routes.Any())
            {
                user.Player.MsgLocStr("No multi-stop routes found. Try building more stores with compatible trade opportunities.");
                return;
            }

            TransportUIBuilder.ShowRoutesPanel(user.Player, user, routes);
        }

        [ChatSubCommand("Transport", "Show detailed info for a specific product", "detail", ChatAuthorizationLevel.User)]
        public static void Detail(User user, string productName = "")
        {
            if (!HasRequiredLogisticsSkill(user))
            {
                ShowSkillRequiredMessage(user);
                return;
            }

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

            EcoTransportModPlugin.StatsService.RecordUsage(user, "detail");
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
