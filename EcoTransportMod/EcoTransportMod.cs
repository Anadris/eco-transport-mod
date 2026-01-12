// EcoTransportMod - Economy Data Mod for Eco 12.0.6
// Provides economy/market data via chat commands, native UI panels and file export

namespace Eco.Mods.EcoTransportMod
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
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
    using Eco.Gameplay.Components.Auth;
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
            return GetSellToStorageCapacity() > 0;
        }

        /// <summary>
        /// Checks if a specific storage can accept an item type based on storage restrictions
        /// Returns true if the storage can accept the item, false if restricted
        /// Also checks if "Put Into" permission is enabled via AuthorizationFlags
        /// </summary>
        public static bool CanStorageAcceptItemType(PublicStorageComponent storage, Item item)
        {
            if (storage?.Parent == null || item == null) return false;

            // Check if storage is enabled
            if (!storage.Enabled)
                return false;

            // Check if inventory has "Put Into" permission (AuthedMayAdd flag)
            if (storage.Inventory != null)
            {
                try
                {
                    var authProperty = storage.Inventory.GetType().GetProperty("Authorizations");
                    if (authProperty != null)
                    {
                        var authValue = authProperty.GetValue(storage.Inventory);
                        if (authValue != null)
                        {
                            // Check if AuthedMayAdd flag is set (allows putting items in)
                            var authFlags = authValue.ToString();
                            if (!authFlags.Contains("AuthedMayAdd"))
                            {
                                return false; // "Put Into" is disabled
                            }
                        }
                    }
                }
                catch
                {
                    // If we can't check authorization, assume it's allowed
                }
            }

            // Check inventory restrictions directly
            if (storage.Inventory != null)
            {
                try
                {
                    var restrictionsProperty = storage.Inventory.GetType().GetProperty("Restrictions");
                    if (restrictionsProperty != null)
                    {
                        var restrictions = restrictionsProperty.GetValue(storage.Inventory) as IEnumerable<object>;
                        if (restrictions != null)
                        {
                            foreach (var restriction in restrictions)
                            {
                                if (restriction == null) continue;

                                var restrictionType = restriction.GetType().Name;

                                // NotCarriedRestriction - cannot store Carried items (logs, rocks, etc.)
                                if (restrictionType.Contains("NotCarriedRestriction"))
                                {
                                    bool isCarriedItem = item.Category == "Block" || item.IsCarried;
                                    if (isCarriedItem)
                                        return false; // Chest/container cannot accept Carried items
                                }

                                // FoodStorageRestriction - only food items
                                else if (restrictionType.Contains("FoodStorageRestriction"))
                                {
                                    bool isFoodItem = item.Category == "Food";
                                    if (!isFoodItem)
                                        return false; // Icebox/Refrigerator only accepts food
                                }

                                // ClothItemRestriction - only clothing/tools
                                else if (restrictionType.Contains("ClothItemRestriction"))
                                {
                                    bool isClothItem = item.Category == "Clothing" || item.Category == "Tool";
                                    if (!isClothItem)
                                        return false; // Dresser only accepts clothing
                                }

                                // FuelStorageRestriction - only fuel items
                                else if (restrictionType.Contains("FuelStorageRestriction") || restrictionType.Contains("FuelRestriction"))
                                {
                                    // Check if item has Fuel tag or is in Fuel category
                                    bool isFuelItem = item.Category == "Fuel" || item.Tags().Any(tag => tag.Name == "Fuel");
                                    if (!isFuelItem)
                                        return false; // Fuel storage only accepts fuel
                                }

                                // SeedRestriction - only seed items (for planters/pots)
                                else if (restrictionType.Contains("SeedRestriction"))
                                {
                                    // Check if item is a seed (has "Seed" in name or tags)
                                    bool isSeedItem = item.DisplayName.ToString().Contains("Seed") ||
                                                      item.Tags().Any(tag => tag.Name.Contains("Seed"));
                                    if (!isSeedItem)
                                        return false; // Planter/Pot only accepts seeds
                                }

                                // WeightRestriction - storage has weight limit (carts, etc.)
                                // We can't easily check current weight, so we'll be conservative
                                // and not count these as valid for capacity calculation
                                else if (restrictionType.Contains("WeightRestriction"))
                                {
                                    // For items that are carried (logs, blocks), weight restriction
                                    // likely means the cart could be weight-limited
                                    // Skip these storages for carried items to be safe
                                    if (item.IsCarried)
                                        return false;
                                }

                                // StackLimitRestriction - limits stack size, but doesn't prevent storage
                                // We handle this by not blocking, but it may affect capacity calculations
                            }
                        }
                    }
                }
                catch
                {
                    // If we can't check restrictions, fall back to default behavior
                }
            }

            // No restrictions blocked the item, it can be accepted
            return true;
        }

        /// <summary>
        /// Gets all individual storage components from a store's linked chain
        /// Uses Reflection to access LinkedObjects property
        /// </summary>
        public static List<PublicStorageComponent> GetAllLinkedStorages(WorldObject store)
        {
            var result = new List<PublicStorageComponent>();

            // Add the store's own storage first
            var ownStorage = store.GetComponent<PublicStorageComponent>();
            if (ownStorage != null)
                result.Add(ownStorage);

            var linkComponent = store.GetComponent<LinkComponent>();
            if (linkComponent == null) return result;

            // Use Reflection to access LinkedObjects (it's a ConcurrentHashSet)
            var linkedObjectsProp = typeof(LinkComponent).GetProperty("LinkedObjects",
                BindingFlags.Instance | BindingFlags.Public);

            if (linkedObjectsProp?.GetValue(linkComponent) is not IEnumerable linkedObjects)
                return result;

            // Create snapshot to avoid concurrent modification
            var snapshot = new List<LinkComponent>();
            foreach (var obj in linkedObjects)
            {
                if (obj is LinkComponent lc)
                    snapshot.Add(lc);
            }

            // Iterate through each linked object
            foreach (var linkedComp in snapshot)
            {
                if (linkedComp?.Parent == null || linkedComp.Parent.IsDestroyed)
                    continue;

                // Get storage component from linked object
                var storage = linkedComp.Parent.GetComponent<PublicStorageComponent>();
                if (storage != null)
                    result.Add(storage);
            }

            return result;
        }

        /// <summary>
        /// Gets the available storage capacity for this item in the sell-to store's linked storages.
        /// Returns the number of items that can be stored, considering:
        /// - Storage restrictions (NotCarried, Food, Seed, Weight, etc.)
        /// - Put Into permission (AuthedMayAdd)
        /// - Partial stacks of the same item
        /// - Empty slots in valid storages
        /// Returns 0 if no capacity available (FULL).
        /// </summary>
        public int GetSellToStorageCapacity()
        {
            if (SellTo?.Store == null || SellTo.ItemType == null)
                return 0;

            var item = Item.Get(SellTo.ItemType);
            if (item == null)
                return 0;

            var allStorages = GetAllLinkedStorages(SellTo.Store);
            if (!allStorages.Any())
                return 0;

            int maxStackSize = item.MaxStackSize;
            int availableCapacity = 0;

            foreach (var storage in allStorages)
            {
                if (storage?.Inventory == null) continue;

                // Check if this storage can accept the item (restrictions + permissions)
                if (!CanStorageAcceptItemType(storage, item))
                    continue;

                var stacks = storage.Inventory.Stacks.ToList();

                // Add capacity from partial stacks of the same item
                foreach (var stack in stacks)
                {
                    if (stack?.Item?.Type == SellTo.ItemType && stack.Quantity < maxStackSize)
                    {
                        availableCapacity += (maxStackSize - stack.Quantity);
                    }
                }

                // Count empty slots
                int emptySlots = stacks.Count(s => s.Empty());
                bool hasThisItem = stacks.Any(s => s?.Item?.Type == SellTo.ItemType);
                bool isEmpty = stacks.All(s => s.Empty());

                // Add capacity from empty slots if storage already has this item or is empty
                if (hasThisItem || isEmpty)
                {
                    availableCapacity += emptySlots * maxStackSize;
                }
            }

            return availableCapacity;
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

        /// <summary>
        /// Checks if a user has access to the sell-to store
        /// Uses PropertyAuthComponent to check if store is public or user has consumer access
        /// </summary>
        /// <param name="user">The user to check access for</param>
        /// <returns>True if user can access the store, false otherwise</returns>
        public bool CanUserAccessSellToStore(User user)
        {
            if (SellTo?.Store == null || user == null)
                return false;

            try
            {
                var authComp = SellTo.Store.GetComponent<PropertyAuthComponent>();
                if (authComp == null)
                    return true; // No auth component = public access

                // Check IsPublicProperty via reflection
                var isPublicProp = authComp.GetType().GetProperty("IsPublicProperty", BindingFlags.Instance | BindingFlags.Public);
                if (isPublicProp != null)
                {
                    var isPublic = isPublicProp.GetValue(authComp) as bool?;
                    if (isPublic == true)
                        return true;
                }

                // Check UsersWithConsumerAccess - if user is in this list, they have access
                var consumerAccessProp = authComp.GetType().GetProperty("UsersWithConsumerAccess", BindingFlags.Instance | BindingFlags.Public);
                if (consumerAccessProp != null)
                {
                    var usersWithAccess = consumerAccessProp.GetValue(authComp) as IEnumerable;
                    if (usersWithAccess != null)
                    {
                        foreach (var u in usersWithAccess)
                        {
                            if (u == user) return true;
                        }
                    }
                }

                return false;
            }
            catch
            {
                return true; // On error, allow access to not block legitimate trades
            }
        }

        /// <summary>
        /// Gets a debug string for store access status (for UI display)
        /// </summary>
        public string GetAccessDebugStatus(User user)
        {
            if (SellTo?.Store == null || user == null)
                return "Store/User null";

            try
            {
                var authComp = SellTo.Store.GetComponent<PropertyAuthComponent>();
                if (authComp == null)
                    return "No Auth (OK)";

                // Check IsPublicProperty
                var isPublicProp = authComp.GetType().GetProperty("IsPublicProperty", BindingFlags.Instance | BindingFlags.Public);
                if (isPublicProp != null)
                {
                    var isPublic = isPublicProp.GetValue(authComp) as bool?;
                    if (isPublic == true)
                        return "Public";
                }

                // Check UsersWithConsumerAccess
                var consumerAccessProp = authComp.GetType().GetProperty("UsersWithConsumerAccess", BindingFlags.Instance | BindingFlags.Public);
                if (consumerAccessProp != null)
                {
                    var usersWithAccess = consumerAccessProp.GetValue(authComp) as IEnumerable;
                    if (usersWithAccess != null)
                    {
                        foreach (var u in usersWithAccess)
                        {
                            if (u == user) return "Consumer";
                        }
                    }
                }

                return "No Access";
            }
            catch (Exception ex)
            {
                return $"Err: {ex.Message}";
            }
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
                // Check if store is active (turned On)
                var onOffComponent = worldObject.GetComponent<OnOffComponent>();
                if (onOffComponent != null && !onOffComponent.On)
                    return; // Store is turned Off, skip it

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

                                // Create opportunity to check access
                                var opportunity = new TradeOpportunity
                                {
                                    BuyFrom = buyFrom,
                                    SellTo = sellTo
                                };

                                // FIRST CHECK: Verify user has access to sell-to store
                                if (!opportunity.CanUserAccessSellToStore(requestingUser))
                                    continue;

                                opportunities.Add(opportunity);
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

                // Get storage capacity for sell-to store
                int storageCapacity = opp.GetSellToStorageCapacity();

                content.AppendLine(Localizer.Do($"    - {opp.BuyFrom.GetStoreLink()}  →  {opp.SellTo.GetStoreLink()}"));

                // Build storage status message
                string storageStatus;
                if (storageCapacity == 0)
                {
                    storageStatus = Text.Color("#FF6B6B", "FULL");
                }
                else if (storageCapacity < opp.MaxQuantity)
                {
                    storageStatus = Text.Color("#FFA500", $"Limited to {storageCapacity}");
                }
                else
                {
                    storageStatus = Text.Positive($"{storageCapacity}");
                }

                // Get access status for display
                var accessStatus = opp.GetAccessDebugStatus(user);
                var accessColor = (accessStatus == "Public" || accessStatus == "Consumer" || accessStatus == "No Auth (OK)") ? "#9CCD4F" : "#FF6B6B";

                var affordMsg = canAfford ? "" : $"  {Text.Color(totalCostColor, "(you only have " + Text.StyledNum(userBalance) + ")")}";
                content.AppendLine(Localizer.Do($"      Buy x {Text.Info($"{opp.MaxQuantity}")} at {Text.Positive(Text.Bold(Text.StyledNum(opp.BuyFrom.Price)))} {currencyLink}            →            Sell at {Text.Positive(Text.Bold(Text.StyledNum(opp.SellTo.Price)))} {currencyLink}"));
                content.AppendLine(Localizer.Do($"      Total Investment:   {Text.Color(totalCostColor, Text.Bold(opp.TotalCost))} {currencyLink} {affordMsg} "));
                content.AppendLine(Localizer.Do($"      Storage: {Text.Bold(storageStatus)}"));
                content.AppendLine(Localizer.Do($"      Access: {Text.Color(accessColor, accessStatus)}"));
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
            int storageCapacity = opp.GetSellToStorageCapacity();
            string storageText;
            if (storageCapacity == 0)
            {
                storageText = Text.Color("#FF6B6B", "FULL - Cannot accept items");
            }
            else if (storageCapacity < opp.MaxQuantity)
            {
                storageText = Text.Color("#FFA500", $"{storageCapacity} items can fit");
            }
            else
            {
                storageText = Text.Positive($"{storageCapacity} items can fit");
            }
            content.AppendLine(Localizer.Do($"Storage: {storageText}"));

            // Access info
            var accessStatus = opp.GetAccessDebugStatus(user);
            var accessColor = (accessStatus == "Public" || accessStatus == "Consumer" || accessStatus == "No Auth (OK)") ? "#9CCD4F" : "#FF6B6B";
            content.AppendLine(Localizer.Do($"Access: {Text.Color(accessColor, accessStatus)}"));
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
                    int storageCapacity = opp.GetSellToStorageCapacity();
                    string storageStatus;
                    if (storageCapacity == 0)
                    {
                        storageStatus = Text.Color("#FF6B6B", "FULL");
                    }
                    else if (storageCapacity < opp.MaxQuantity)
                    {
                        storageStatus = Text.Color("#FFA500", $"{storageCapacity}");
                    }
                    else
                    {
                        storageStatus = Text.Positive($"{storageCapacity}");
                    }

                    // Access info
                    var accessStatus = opp.GetAccessDebugStatus(user);
                    var accessColor = (accessStatus == "Public" || accessStatus == "Consumer" || accessStatus == "No Auth (OK)") ? "#9CCD4F" : "#FF6B6B";

                    content.AppendLine(Localizer.Do($"    Distance: {Text.Info($"{opp.Distance:F0}")}m    |    Storage: {storageStatus}    |    Access: {Text.Color(accessColor, accessStatus)}    |    Profit: {Text.Positive(Text.Bold(Text.StyledNum(opp.TotalProfit)))} {currencyLink}"));
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
