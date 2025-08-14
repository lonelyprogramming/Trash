using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Serilog;

// 刷新任务类型
public enum RefreshTaskType
{
    Standard,      // 标准定时刷新
    Force,         // 强制刷新
    Adaptive       // 自适应刷新
}

// 新增：数据变化检测器接口，将比较逻辑解耦
public interface IDataChangeDetector<T>
{
    bool HasChanged(T oldValue, T newValue);
    double CalculateChangeRate(T oldValue, T newValue);
}

public class AFAttributeEntryChangeDetector : IDataChangeDetector<AFAttributeEntry>
{
    /// <summary>
    /// 比较两个 AFAttributeEntry 对象的内部 Value 属性。
    /// </summary>
    public bool HasChanged(AFAttributeEntry oldValue, AFAttributeEntry newValue)
    {
        // **核心逻辑：安全地提取嵌套属性值**
        // 它会自动处理中间环节的 null，如果链条中任何一环是 null，整个表达式就返回 null。
        var oldVal = oldValue?.AFAttributeData?.Value;
        var newVal = newValue?.AFAttributeData?.Value;

        // 现在，我们只需要比较最终提取出的 oldVal 和 newVal 即可。
        
        // 1. 处理最终值的 null 情况。
        if (oldVal == null && newVal == null) return false;
        if (oldVal == null || newVal == null) return true;

        // 2. (可选) 为特定类型（如 double）添加特殊处理，以应对浮点数精度问题。
        if (oldVal is double oldDouble && newVal is double newDouble)
        {
            // 使用一个极小的容差 (epsilon) 来比较 double。
            return Math.Abs(oldDouble - newDouble) >= 1e-4;
        }

        // 3. 对于所有其他类型，使用默认的 object.Equals 进行比较。
        return !object.Equals(oldVal, newVal);
    }

    /// <summary>
    /// 计算一个介于 0.0 和 1.0 之间的“变化率”。
    /// </summary>
    public double CalculateChangeRate(AFAttributeEntry oldValue, AFAttributeEntry newValue)
    {
        // 为了简单，如果数据有变动则返回 1.0，否则返回 0.0。
        // 您可以根据业务需求实现更复杂的逻辑。
        return HasChanged(oldValue, newValue) ? 1.0 : 0.0;
    }
}

// 简化的CacheItem - 内置变化检测逻辑
public class CacheItem<T>
{
    public T Value { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public long AccessCount; 
    public DateTime ExpiresAt { get; set; }
    public bool IsAutoRefreshCandidate { get; set; } = true;
    public bool IsStale { get; set; } = false;
    public DateTime LastRefreshedAt { get; set; } = DateTime.MinValue;
    
    // 自适应刷新字段
    public DateTime LastDataChangeCheckTime { get; set; } = DateTime.MinValue;
    public TimeSpan EstimatedDataChangeInterval { get; set; } = TimeSpan.FromMinutes(15);
    
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    
    public bool ShouldRefresh(double threshold)
    {
        var totalLifetime = ExpiresAt - CreatedAt;
        var currentAge = DateTime.UtcNow - CreatedAt;
        return currentAge.TotalMilliseconds >= totalLifetime.TotalMilliseconds * threshold;
    }

    public bool ShouldForceRefresh(TimeSpan forceRefreshInterval)
    {
        var lastRefresh = LastRefreshedAt == DateTime.MinValue ? CreatedAt : LastRefreshedAt;
        return DateTime.UtcNow - lastRefresh >= forceRefreshInterval;
    }
    
    public bool ShouldAdaptiveRefresh()
    {
        var now = DateTime.UtcNow;
        var lastCheck = LastDataChangeCheckTime == DateTime.MinValue ? CreatedAt : LastDataChangeCheckTime;
        return (now - lastCheck) >= EstimatedDataChangeInterval;
    }
    
    // 更新自适应间隔
    public void UpdateAdaptiveInterval(T newValue, IDataChangeDetector<T> changeDetector, ILogger logger = null)
    {
        var now = DateTime.UtcNow;
        var hasChanged = changeDetector.HasChanged(this.Value, newValue);
        
        if (hasChanged)
        {
            // 数据发生变化，直接重置为默认的10秒间隔
            EstimatedDataChangeInterval = TimeSpan.FromSeconds(10);
            
            logger?.Debug("Data changed, interval reset to {Interval}", 
                          EstimatedDataChangeInterval);
        }
        else
        {
            // 数据未变化，逐渐延长检查间隔
            var timeSinceLastCheck = now - LastDataChangeCheckTime;
            var growthFactor = Math.Min(1.5, 1.0 + (timeSinceLastCheck.TotalMinutes / 120.0)); // 最多延长1.5倍
            
            EstimatedDataChangeInterval = TimeSpan.FromTicks(
                Math.Min(TimeSpan.FromHours(2).Ticks, // 最大2小时
                        (long)(EstimatedDataChangeInterval.Ticks * growthFactor)));
                        
            logger?.Debug("Data unchanged, extended interval to {Interval}", EstimatedDataChangeInterval);
        }
        
        LastDataChangeCheckTime = now;
    }
}

// 简化的刷新调度器 - 专注于批量刷新
public class RefreshScheduler<TKey, TValue> : IDisposable
{
    private readonly IDataLoader<TKey, TValue> _dataLoader;
    private readonly IDataChangeDetector<TValue> _changeDetector;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly CacheConfig _config;
    private readonly ILogger _logger;
    private readonly Func<IEnumerable<TKey>> _getKeysToRefresh;
    private readonly Func<TKey, CacheItem<TValue>> _getCacheItem;
    private readonly Action<TKey, TValue, bool> _updateCache;
    
    private readonly Timer _refreshTimer;
    private readonly SemaphoreSlim _refreshSemaphore;
    private volatile bool _disposed = false;
    
    // 统计
    private long _totalRefreshBatches;
    private long _successfulRefreshes;
    private long _failedRefreshes;
    private long _adaptiveIntervalAdjustments;

    public long TotalRefreshBatches => Interlocked.Read(ref _totalRefreshBatches);
    public long SuccessfulRefreshes => Interlocked.Read(ref _successfulRefreshes);
    public long FailedRefreshes => Interlocked.Read(ref _failedRefreshes);
    public long AdaptiveIntervalAdjustments => Interlocked.Read(ref _adaptiveIntervalAdjustments);

    public RefreshScheduler(
        IDataLoader<TKey, TValue> dataLoader,
        IDataChangeDetector<TValue> changeDetector,
        CircuitBreaker circuitBreaker,
        CacheConfig config,
        ILogger logger,
        Func<IEnumerable<TKey>> getKeysToRefresh,
        Func<TKey, CacheItem<TValue>> getCacheItem,
        Action<TKey, TValue, bool> updateCache)
    {
        _dataLoader = dataLoader ?? throw new ArgumentNullException(nameof(dataLoader));
        _changeDetector = changeDetector ?? throw new ArgumentNullException(nameof(changeDetector));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
        _getKeysToRefresh = getKeysToRefresh ?? throw new ArgumentNullException(nameof(getKeysToRefresh));
        _getCacheItem = getCacheItem ?? throw new ArgumentNullException(nameof(getCacheItem));
        _updateCache = updateCache ?? throw new ArgumentNullException(nameof(updateCache));
        
        _refreshSemaphore = new SemaphoreSlim(1, 1);
        
        // 根据配置启动对应的刷新定时器
        StartRefreshTimer();
    }

    private void StartRefreshTimer()
    {
        TimeSpan interval = TimeSpan.FromMinutes(1); // 默认间隔
        
        if (_config.EnableForceRefresh)
        {
            interval = _config.ForceRefreshInterval;
        }
        else if (_config.EnableAutoRefresh)
        {
            interval = _config.AutoRefreshInterval;
        }
        else if (_config.EnableAdaptiveRefresh)
        {
            interval = TimeSpan.FromSeconds(10); // 自适应刷新检查更频繁
        }
        
        _refreshTimer = new Timer(ExecuteRefreshCycle, null, interval, interval);
        _logger?.Information("Started refresh timer with interval: {Interval}", interval);
    }

    private async void ExecuteRefreshCycle(object state)
    {
        if (_disposed || _circuitBreaker.State == CircuitBreakerState.Open) return;
        
        if (!await _refreshSemaphore.WaitAsync(100)) // 避免重叠执行
            return;
        
        try
        {
            await PerformBatchRefresh();
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Error in refresh cycle");
        }
        finally
        {
            _refreshSemaphore.Release();
        }
    }

    private async Task PerformBatchRefresh()
    {
        var keysToRefresh = _getKeysToRefresh().ToList();
        if (!keysToRefresh.Any()) return;

        // 根据配置应用不同的过滤逻辑
        keysToRefresh = ApplyRefreshFilter(keysToRefresh);
        if (!keysToRefresh.Any()) return;

        // 分批处理
        var batchSize = Math.Min(keysToRefresh.Count, _config.MaxAutoRefreshBatchSize);
        var batches = keysToRefresh
            .Select((key, index) => new { key, index })
            .GroupBy(x => x.index / batchSize)
            .Select(g => g.Select(x => x.key).ToList())
            .ToList();

        Interlocked.Increment(ref _totalRefreshBatches);
        
        foreach (var batch in batches)
        {
            await ProcessBatch(batch);
        }
    }

    private List<TKey> ApplyRefreshFilter(List<TKey> allKeys)
    {
        var filteredKeys = new List<TKey>();

        foreach (var key in allKeys)
        {
            var cacheItem = _getCacheItem(key);
            if (cacheItem == null || cacheItem.IsExpired || !cacheItem.IsAutoRefreshCandidate)
                continue;

            bool shouldRefresh = false;
            
            if (_config.EnableForceRefresh)
            {
                shouldRefresh = cacheItem.ShouldForceRefresh(_config.ForceRefreshInterval);
            }
            else if (_config.EnableAdaptiveRefresh)
            {
                shouldRefresh = cacheItem.ShouldAdaptiveRefresh();
            }
            else if (_config.EnableAutoRefresh)
            {
                shouldRefresh = cacheItem.ShouldRefresh(_config.RefreshThreshold);
            }

            if (shouldRefresh)
            {
                filteredKeys.Add(key);
            }
        }

        // 应用刷新策略对所有符合条件的键进行排序，然后返回完整列表
        return ApplyRefreshStrategy(filteredKeys).ToList();
    }

    private IEnumerable<TKey> ApplyRefreshStrategy(List<TKey> keys)
    {
        var keyItems = keys.Select(key => new { Key = key, Item = _getCacheItem(key) })
                          .Where(x => x.Item != null)
                          .ToList();

        return _config.AutoRefreshStrategy switch
        {
            AutoRefreshStrategy.All => keyItems.Select(x => x.Key),
            AutoRefreshStrategy.LeastRecentlyUsed => keyItems
                .OrderBy(x => x.Item.LastAccessedAt)
                .Select(x => x.Key),
            AutoRefreshStrategy.MostFrequentlyUsed => keyItems
                .OrderByDescending(x => Interlocked.Read(ref x.Item.AccessCount))
                .Select(x => x.Key),
            AutoRefreshStrategy.OldestFirst => keyItems
                .OrderBy(x => x.Item.CreatedAt)
                .Select(x => x.Key),
            AutoRefreshStrategy.ExpirationBased => keyItems
                .OrderBy(x => x.Item.ExpiresAt)
                .Select(x => x.Key),
            AutoRefreshStrategy.AdaptivePriority => keyItems
                .Where(x => x.Item.ShouldAdaptiveRefresh())
                .OrderBy(x => x.Item.EstimatedDataChangeInterval)
                .Select(x => x.Key),
            _ => keyItems.Select(x => x.Key)
        };
    }

    private async Task ProcessBatch(List<TKey> batch)
    {
        try
        {
            var newValues = await _circuitBreaker.ExecuteAsync(async () =>
            {
                return await _dataLoader.LoadBatchAsync(batch);
            });

            foreach (var kvp in newValues)
            {
                await ProcessSingleRefreshResult(kvp.Key, kvp.Value);
                Interlocked.Increment(ref _successfulRefreshes);
            }

            _logger?.Debug("Successfully processed batch refresh for {Count} keys", newValues.Count);
        }
        catch (CircuitBreakerOpenException)
        {
            _logger?.Warning("Batch refresh cancelled due to circuit breaker open");
            Interlocked.Add(ref _failedRefreshes, batch.Count);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to process batch refresh for {Count} keys", batch.Count);
            Interlocked.Add(ref _failedRefreshes, batch.Count);
        }
    }

    private async Task ProcessSingleRefreshResult(TKey key, TValue newValue)
    {
        var cacheItem = _getCacheItem(key);
        if (cacheItem == null) return;

        var hasChanged = _changeDetector.HasChanged(cacheItem.Value, newValue);

        // 如果是自适应刷新，更新间隔
        if (_config.EnableAdaptiveRefresh)
        {
            cacheItem.UpdateAdaptiveInterval(newValue, _changeDetector, _logger);
            Interlocked.Increment(ref _adaptiveIntervalAdjustments);
        }
        
        // 更新缓存
        _updateCache(key, newValue, true);
        
        _logger?.Debug("Processed refresh for key '{Key}', data changed: {Changed}",
                      key, hasChanged);
    }

    // 获取调度器统计信息
    public RefreshSchedulerStatistics GetStatistics()
    {
        return new RefreshSchedulerStatistics
        {
            TotalRefreshBatches = this.TotalRefreshBatches,
            SuccessfulRefreshes = this.SuccessfulRefreshes,
            FailedRefreshes = this.FailedRefreshes,
            AdaptiveIntervalAdjustments = this.AdaptiveIntervalAdjustments,
            IsRunning = !_disposed,
            RefreshMode = GetCurrentRefreshMode(),
            SuccessRate = this.SuccessfulRefreshes + this.FailedRefreshes > 0 ? 
                (double)this.SuccessfulRefreshes / (this.SuccessfulRefreshes + this.FailedRefreshes) : 1.0
        };
    }

    private string GetCurrentRefreshMode()
    {
        if (_config.EnableForceRefresh) return "Force";
        if (_config.EnableAdaptiveRefresh) return "Adaptive";
        if (_config.EnableAutoRefresh) return "Standard";
        return "None";
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _refreshTimer?.Dispose();
        _refreshSemaphore?.Dispose();
    }
}

// 调度器统计信息
public class RefreshSchedulerStatistics
{
    public long TotalRefreshBatches { get; set; }
    public long SuccessfulRefreshes { get; set; }
    public long FailedRefreshes { get; set; }
    public long AdaptiveIntervalAdjustments { get; set; }
    public bool IsRunning { get; set; }
    public string RefreshMode { get; set; }
    public double SuccessRate { get; set; }
}

// 简化的缓存配置
public class CacheConfig
{
    public LoadStrategy LoadStrategy { get; set; } = LoadStrategy.ResponseFirst;
    public CacheStrategy CacheStrategy { get; set; } = CacheStrategy.LRU;
    public int MaxCacheSize { get; set; } = 10000;
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(30);
    public int BatchSize { get; set; } = 100;
    public TimeSpan BatchWindow { get; set; } = TimeSpan.FromMilliseconds(50);
    public int MaxConcurrentBatches { get; set; } = 10;
    
    // 自动刷新配置 - 每次只能启用一种模式
    public bool EnableAutoRefresh { get; set; } = false;
    public TimeSpan AutoRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
    public double RefreshThreshold { get; set; } = 0.8;
    public int MaxAutoRefreshBatchSize { get; set; } = 50;
    public AutoRefreshStrategy AutoRefreshStrategy { get; set; } = AutoRefreshStrategy.LeastRecentlyUsed;
    
    // 强制刷新配置
    public bool EnableForceRefresh { get; set; } = false;
    public TimeSpan ForceRefreshInterval { get; set; } = TimeSpan.FromMinutes(5);
    
    // 自适应刷新配置
    public bool EnableAdaptiveRefresh { get; set; } = false;
    
    // 熔断器配置
    public CircuitBreakerConfig CircuitBreaker { get; set; } = new CircuitBreakerConfig();
    
    // 降级策略配置
    public bool EnableStaleDataFallback { get; set; } = true;
    public TimeSpan MaxStaleDataAge { get; set; } = TimeSpan.FromHours(1);
    
    // 配置验证
    public void Validate()
    {
        var enabledModes = new[] { EnableAutoRefresh, EnableForceRefresh, EnableAdaptiveRefresh };
        if (enabledModes.Count(x => x) > 1)
        {
            throw new InvalidOperationException("只能启用一种刷新模式");
        }
    }
}

// 主缓存类 - 专注于处理外部查询
public partial class ImprovedAdvancedMemoryCache<TKey, TValue> : IDisposable
{
    private IMemoryCache _memoryCache;
    private readonly IDataLoader<TKey, TValue> _dataLoader;
    private readonly CacheConfig _config;
    private readonly Func<TKey, string> _keySerializer;
    private readonly ILogger _logger;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RefreshScheduler<TKey, TValue> _refreshScheduler;

    private readonly ConcurrentDictionary<TKey, Task<TValue>> _loadingTasks = new();
    private readonly ConcurrentDictionary<TKey, CacheItem<TValue>> _staleDataStorage = new();
    private readonly ConcurrentDictionary<TKey, byte> _cacheKeys = new();
    private volatile bool _disposed = false;

    public ImprovedAdvancedMemoryCache(
        IDataLoader<TKey, TValue> dataLoader,
        IOptions<CacheConfig> config,
        ILogger logger = null,
        Func<TKey, string> keySerializer = null,
        IDataChangeDetector<TValue> changeDetector = null)
    {
        _dataLoader = dataLoader ?? throw new ArgumentNullException(nameof(dataLoader));
        _config = config?.Value ?? new CacheConfig();
        _config.Validate(); // 验证配置
        
        var effectiveChangeDetector = changeDetector ?? new GenericChangeDetector<TValue>();

        _logger = logger?.ForContext<ImprovedAdvancedMemoryCache<TKey, TValue>>();
        _keySerializer = keySerializer ?? (key => key.ToString());
        
        _circuitBreaker = new CircuitBreaker(_config.CircuitBreaker, _logger);
        
        // 创建刷新调度器
        _refreshScheduler = new RefreshScheduler<TKey, TValue>(
            _dataLoader,
            effectiveChangeDetector,
            _circuitBreaker,
            _config,
            _logger,
            GetAllCacheKeys,      // 获取所有需要刷新的键
            GetCacheItemForScheduler,
            SetCacheForScheduler
        );

        _memoryCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _config.MaxCacheSize
        });

        _logger?.Information("Cache initialized with refresh mode: {Mode}", 
                           _refreshScheduler.GetStatistics().RefreshMode);
    }

    // 获取数据的主要方法
    public async Task<TValue> GetAsync(TKey key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ImprovedAdvancedMemoryCache<TKey, TValue>));
        
        var cacheKey = GetCacheKey(key);
        
        // 尝试从缓存获取
        if (_memoryCache.TryGetValue(cacheKey, out CacheItem<TValue> cachedItem))
        {
            if (!cachedItem.IsExpired)
            {
                // 更新访问统计
                cachedItem.LastAccessedAt = DateTime.UtcNow;
                Interlocked.Increment(ref cachedItem.AccessCount);
                
                _logger?.Debug("Cache hit for key '{Key}'", key);
                return cachedItem.Value;
            }
        }
        
        // 缓存未命中或已过期，需要加载数据
        return await LoadDataAsync(key);
    }

    private async Task<TValue> LoadDataAsync(TKey key)
    {
        // 防止重复加载
        if (_loadingTasks.TryGetValue(key, out var existingTask))
        {
            return await existingTask;
        }
        
        var loadingTask = LoadFromDataSourceAsync(key);
        _loadingTasks[key] = loadingTask;
        
        try
        {
            var result = await loadingTask;
            SetCache(key, result, false);
            return result;
        }
        catch (Exception ex)
        {
            // 尝试返回陈旧数据
            if (_config.EnableStaleDataFallback && 
                _staleDataStorage.TryGetValue(key, out var staleItem))
            {
                var staleAge = DateTime.UtcNow - staleItem.CreatedAt;
                if (staleAge <= _config.MaxStaleDataAge)
                {
                    _logger?.Warning("Returning stale data for key '{Key}' due to load failure: {Error}", key, ex.Message);
                    return staleItem.Value;
                }
            }
            
            throw;
        }
        finally
        {
            _loadingTasks.TryRemove(key, out _);
        }
    }

    private async Task<TValue> LoadFromDataSourceAsync(TKey key)
    {
        return await _circuitBreaker.ExecuteAsync(async () =>
        {
            return await _dataLoader.LoadAsync(key);
        });
    }

    // 获取所有缓存键 - 供调度器使用
    private IEnumerable<TKey> GetAllCacheKeys()
    {
        return _cacheKeys.Keys.ToList();
    }

    // 调度器需要的辅助方法
    private CacheItem<TValue> GetCacheItemForScheduler(TKey key)
    {
        var cacheKey = GetCacheKey(key);
        _memoryCache.TryGetValue(cacheKey, out CacheItem<TValue> item);
        return item;
    }

    private void SetCacheForScheduler(TKey key, TValue value, bool isRefresh)
    {
        SetCache(key, value, isRefresh);
    }

    private void SetCache(TKey key, TValue value, bool isRefresh = false)
    {
        var cacheKey = GetCacheKey(key);
        var now = DateTime.UtcNow;

        var cacheItem = new CacheItem<TValue>
        {
            Value = value,
            CreatedAt = now,
            LastAccessedAt = now,
            AccessCount = 1,
            ExpiresAt = now.Add(_config.DefaultExpiration),
            IsAutoRefreshCandidate = true,
            IsStale = false,
            LastRefreshedAt = now
        };

        // 如果是刷新操作，保留原有的一些统计信息
        if (isRefresh && _memoryCache.TryGetValue(cacheKey, out CacheItem<TValue> existingItem))
        {
            cacheItem.CreatedAt = existingItem.CreatedAt;
            cacheItem.LastAccessedAt = existingItem.LastAccessedAt;
            cacheItem.AccessCount = Interlocked.Read(ref existingItem.AccessCount);
            cacheItem.IsAutoRefreshCandidate = existingItem.IsAutoRefreshCandidate;
            cacheItem.EstimatedDataChangeInterval = existingItem.EstimatedDataChangeInterval;
            cacheItem.LastDataChangeCheckTime = existingItem.LastDataChangeCheckTime;
        }

        var entryOptions = new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = _config.DefaultExpiration,
            Priority = CacheItemPriority.Normal
        };

        entryOptions.RegisterPostEvictionCallback((k, v, reason, state) =>
        {
            if (reason == EvictionReason.Replaced) return;

            _cacheKeys.TryRemove(key, out _);
            
            // 保存陈旧数据用于降级
            if (_config.EnableStaleDataFallback && v is CacheItem<TValue> item)
            {
                var staleAge = DateTime.UtcNow - item.CreatedAt;
                if (staleAge <= _config.MaxStaleDataAge)
                {
                    item.IsStale = true;
                    _staleDataStorage[key] = item;
                }
            }
        });

        try
        {
            _memoryCache.Set(cacheKey, cacheItem, entryOptions);
            _cacheKeys[key] = 0;
        }
        catch (ObjectDisposedException)
        {
            _logger?.Warning("Attempted to set key '{Key}' on a disposed cache", key);
        }
    }

    private string GetCacheKey(TKey key)
    {
        return $"cache:{typeof(TValue).Name}:{_keySerializer(key)}";
    }

    // 获取综合统计信息
    public ComprehensiveCacheStatistics GetComprehensiveStatistics()
    {
        var refreshStats = _refreshScheduler.GetStatistics();
        var cbStats = _circuitBreaker.GetStatistics();

        return new ComprehensiveCacheStatistics
        {
            CachedItemsCount = _cacheKeys.Count,
            StaleDataCount = _staleDataStorage.Count,
            RefreshSchedulerStats = refreshStats,
            CircuitBreakerStats = cbStats,
            RefreshMode = refreshStats.RefreshMode
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _refreshScheduler?.Dispose();
        _memoryCache?.Dispose();
        
        _loadingTasks.Clear();
        _cacheKeys.Clear();
        _staleDataStorage.Clear();
    }
}

// 综合统计信息
public class ComprehensiveCacheStatistics
{
    public int CachedItemsCount { get; set; }
    public int StaleDataCount { get; set; }
    public RefreshSchedulerStatistics RefreshSchedulerStats { get; set; }
    public CircuitBreakerStatistics CircuitBreakerStats { get; set; }
    public string RefreshMode { get; set; }
}

// 保留的熔断器实现
public enum CircuitBreakerState { Closed, Open, HalfOpen }

public class CircuitBreakerConfig
{
    public bool Enabled { get; set; } = true;
    public int FailureThreshold { get; set; } = 5;
    public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromMinutes(1);
    public int HalfOpenMaxRequests { get; set; } = 3;
    public TimeSpan FailureWindow { get; set; } = TimeSpan.FromMinutes(5);
    public double FailureRateThreshold { get; set; } = 0.5;
    public int MinRequestsInWindow { get; set; } = 10;
}

public class CircuitBreakerStatistics
{
    public CircuitBreakerState State { get; set; }
    public long TotalRequests { get; set; }
    public long SuccessfulRequests { get; set; }
    public long FailedRequests { get; set; }
    public long CircuitOpenRequests { get; set; }
    public double RecentFailureRate { get; set; }
    public DateTime LastStateChangeTime { get; set; }
    public DateTime LastFailureTime { get; set; }
}

public class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string message) : base(message) { }
}

// 熔断器实现
public class CircuitBreaker
{
    private readonly CircuitBreakerConfig _config;
    private readonly ILogger _logger;
    private volatile CircuitBreakerState _state = CircuitBreakerState.Closed;
    private readonly object _stateLock = new object();
    
    private long _totalRequests;
    private long _successfulRequests;
    private long _failedRequests;
    private long _circuitOpenRequests;
    private long _lastStateChangeTimeTicks = DateTime.UtcNow.Ticks;

    public CircuitBreakerState State => _state;

    public CircuitBreaker(CircuitBreakerConfig config, ILogger logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        if (!_config.Enabled)
        {
            return await operation();
        }

        CheckAndUpdateState();

        if (_state == CircuitBreakerState.Open)
        {
            Interlocked.Increment(ref _circuitOpenRequests);
            throw new CircuitBreakerOpenException("Circuit breaker is in open state");
        }

        Interlocked.Increment(ref _totalRequests);

        try
        {
            var result = await operation();
            OnSuccess();
            return result;
        }
        catch (Exception ex)
        {
            OnFailure();
            throw;
        }
    }

    private void OnSuccess()
    {
        Interlocked.Increment(ref _successfulRequests);
        
        if (_state == CircuitBreakerState.HalfOpen)
        {
            lock (_stateLock)
            {
                if (_state == CircuitBreakerState.HalfOpen)
                {
                    ChangeState(CircuitBreakerState.Closed);
                }
            }
        }
    }

    private void OnFailure()
    {
        Interlocked.Increment(ref _failedRequests);
        
        lock (_stateLock)
        {
            if (ShouldTrip())
            {
                ChangeState(CircuitBreakerState.Open);
            }
        }
    }

    private void CheckAndUpdateState()
    {
        if (_state == CircuitBreakerState.Open)
        {
            var lastChangeTicks = Interlocked.Read(ref _lastStateChangeTimeTicks);
            var lastChangeTime = new DateTime(lastChangeTicks, DateTimeKind.Utc);

            if ((DateTime.UtcNow - lastChangeTime) >= _config.OpenTimeout)
            {
                lock (_stateLock)
                {
                    if (_state == CircuitBreakerState.Open)
                    {
                        ChangeState(CircuitBreakerState.HalfOpen);
                    }
                }
            }
        }
    }

    private bool ShouldTrip()
    {
        var total = Interlocked.Read(ref _totalRequests);
        var failed = Interlocked.Read(ref _failedRequests);
        
        if (total < _config.MinRequestsInWindow) return false;
        
        var failureRate = (double)failed / total;
        return failed >= _config.FailureThreshold || failureRate >= _config.FailureRateThreshold;
    }

    private void ChangeState(CircuitBreakerState newState)
    {
        _state = newState;
        Interlocked.Exchange(ref _lastStateChangeTimeTicks, DateTime.UtcNow.Ticks);
        _logger?.Information("Circuit breaker state changed to {State}", newState);
    }

    public CircuitBreakerStatistics GetStatistics()
    {
        return new CircuitBreakerStatistics
        {
            State = _state,
            TotalRequests = Interlocked.Read(ref _totalRequests),
            SuccessfulRequests = Interlocked.Read(ref _successfulRequests),
            FailedRequests = Interlocked.Read(ref _failedRequests),
            CircuitOpenRequests = Interlocked.Read(ref _circuitOpenRequests),
            RecentFailureRate = Interlocked.Read(ref _totalRequests) > 0 ? 
                (double)Interlocked.Read(ref _failedRequests) / Interlocked.Read(ref _totalRequests) : 0,
            LastStateChangeTime = new DateTime(Interlocked.Read(ref _lastStateChangeTimeTicks), DateTimeKind.Utc)
        };
    }

    public void Reset()
    {
        lock (_stateLock)
        {
            ChangeState(CircuitBreakerState.Closed);
            Interlocked.Exchange(ref _totalRequests, 0);
            Interlocked.Exchange(ref _successfulRequests, 0);
            Interlocked.Exchange(ref _failedRequests, 0);
            Interlocked.Exchange(ref _circuitOpenRequests, 0);
        }
    }
}

// 其他必要的枚举和接口定义
public enum LoadStrategy { ResponseFirst, FreshnessFirst }
public enum CacheStrategy { LRU, LFU, FIFO, TTL }
public enum AutoRefreshStrategy 
{ 
    All, LeastRecentlyUsed, MostFrequentlyUsed, 
    OldestFirst, ExpirationBased, AdaptivePriority 
}

public interface IDataLoader<TKey, TValue>
{
    Task<TValue> LoadAsync(TKey key);
    Task<Dictionary<TKey, TValue>> LoadBatchAsync(IEnumerable<TKey> keys);
}

// 使用示例
public class SimplifiedCacheUsageExample
{
    public static async Task Example()
    {
        Console.WriteLine("=== 简化后的自适应缓存系统演示 ===");

        // 测试不同的刷新模式
        await TestAdaptiveRefreshMode();
        await TestForceRefreshMode();
        await TestStandardRefreshMode();
    }

    private static async Task TestAdaptiveRefreshMode()
    {
        Console.WriteLine("\n--- 测试自适应刷新模式 ---");
        
        var config = new CacheConfig
        {
            MaxCacheSize = 50,
            DefaultExpiration = TimeSpan.FromMinutes(5),
            EnableAdaptiveRefresh = true,
            MaxAutoRefreshBatchSize = 10,
            AutoRefreshStrategy = AutoRefreshStrategy.AdaptivePriority,
            EnableStaleDataFallback = true
        };

        var dataLoader = new TestDoubleDataLoader();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message}{NewLine}")
            .CreateLogger();

        var cache = new ImprovedAdvancedMemoryCache<string, double>(
            dataLoader, Options.Create(config), logger);

        try
        {
            // 预加载一些数据
            var keys = new[] { "stock:AAPL", "stock:GOOGL", "stock:MSFT", "price:oil", "price:gold" };
            
            Console.WriteLine("初始加载数据...");
            foreach (var key in keys)
            {
                var value = await cache.GetAsync(key);
                Console.WriteLine($"Loaded {key}: {value:F4}");
            }

            Console.WriteLine("\n模拟不同变化率的数据访问...");
            
            // 运行2分钟，观察自适应行为
            var endTime = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < endTime)
            {
                // 高变化率数据 - 频繁访问
                await cache.GetAsync("stock:AAPL");
                await cache.GetAsync("stock:GOOGL");
                
                // 中等变化率数据
                if (DateTime.UtcNow.Second % 10 == 0)
                {
                    await cache.GetAsync("price:oil");
                }
                
                // 低变化率数据
                if (DateTime.UtcNow.Second % 20 == 0)
                {
                    await cache.GetAsync("price:gold");
                }

                // 显示统计信息
                if (DateTime.UtcNow.Second % 15 == 0)
                {
                    var stats = cache.GetComprehensiveStatistics();
                    Console.WriteLine($"[Stats] Mode={stats.RefreshMode}, " +
                                    $"Cached={stats.CachedItemsCount}, " +
                                    $"Refreshes={stats.RefreshSchedulerStats.SuccessfulRefreshes}, " +
                                    $"Adjustments={stats.RefreshSchedulerStats.AdaptiveIntervalAdjustments}");
                }

                await Task.Delay(2000);
            }
        }
        finally
        {
            cache.Dispose();
            (logger as IDisposable)?.Dispose();
        }
    }

    private static async Task TestForceRefreshMode()
    {
        Console.WriteLine("\n--- 测试强制刷新模式 ---");
        
        var config = new CacheConfig
        {
            MaxCacheSize = 30,
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EnableForceRefresh = true,
            ForceRefreshInterval = TimeSpan.FromSeconds(20), // 每20秒强制刷新
            MaxAutoRefreshBatchSize = 5
        };

        var dataLoader = new TestDoubleDataLoader();
        var cache = new ImprovedAdvancedMemoryCache<string, double>(
            dataLoader, Options.Create(config));

        try
        {
            var keys = new[] { "sensor:temp", "sensor:humidity", "sensor:pressure" };
            
            // 加载数据
            foreach (var key in keys)
            {
                await cache.GetAsync(key);
            }

            Console.WriteLine("等待强制刷新触发...");
            await Task.Delay(TimeSpan.FromSeconds(45));
            
            var stats = cache.GetComprehensiveStatistics();
            Console.WriteLine($"强制刷新统计: 批次={stats.RefreshSchedulerStats.TotalRefreshBatches}, " +
                            $"成功={stats.RefreshSchedulerStats.SuccessfulRefreshes}");
        }
        finally
        {
            cache.Dispose();
        }
    }

    private static async Task TestStandardRefreshMode()
    {
        Console.WriteLine("\n--- 测试标准刷新模式 ---");
        
        var config = new CacheConfig
        {
            MaxCacheSize = 20,
            DefaultExpiration = TimeSpan.FromMinutes(2),
            EnableAutoRefresh = true,
            AutoRefreshInterval = TimeSpan.FromSeconds(15),
            RefreshThreshold = 0.7, // 当缓存项达到70%生命周期时刷新
            MaxAutoRefreshBatchSize = 8,
            AutoRefreshStrategy = AutoRefreshStrategy.MostFrequentlyUsed
        };

        var dataLoader = new TestDoubleDataLoader();
        var cache = new ImprovedAdvancedMemoryCache<string, double>(
            dataLoader, Options.Create(config));

        try
        {
            var keys = new[] { "metric:cpu", "metric:memory", "metric:disk", "metric:network" };
            
            // 加载并建立访问模式
            for (int i = 0; i < 3; i++)
            {
                foreach (var key in keys)
                {
                    await cache.GetAsync(key);
                }
                await Task.Delay(1000);
            }

            // 等待自动刷新
            await Task.Delay(TimeSpan.FromSeconds(30));
            
            var stats = cache.GetComprehensiveStatistics();
            Console.WriteLine($"标准刷新统计: 成功率={stats.RefreshSchedulerStats.SuccessRate:P1}");
        }
        finally
        {
            cache.Dispose();
        }
    }
}

// 测试用的Double类型数据加载器
public class TestDoubleDataLoader : IDataLoader<string, double>
{
    private readonly Random _random = new Random();
    private readonly Dictionary<string, double> _baseValues = new();
    private int _callCount = 0;

    public async Task<double> LoadAsync(string key)
    {
        Interlocked.Increment(ref _callCount);
        
        // 模拟偶发失败
        if (_callCount % 15 == 0)
        {
            throw new TimeoutException($"Simulated timeout for key: {key}");
        }

        await Task.Delay(_random.Next(50, 200));
        
        // 根据键类型生成不同变化程度的数据
        if (!_baseValues.ContainsKey(key))
        {
            _baseValues[key] = _random.NextDouble() * 100;
        }

        var baseValue = _baseValues[key];
        var changeRate = GetChangeRateForKey(key);
        var change = (_random.NextDouble() - 0.5) * 2 * changeRate * baseValue;
        
        var newValue = Math.Max(0.1, baseValue + change);
        _baseValues[key] = newValue; // 更新基准值
        
        return newValue;
    }

    public async Task<Dictionary<string, double>> LoadBatchAsync(IEnumerable<string> keys)
    {
        var result = new Dictionary<string, double>();
        
        foreach (var key in keys)
        {
            try
            {
                result[key] = await LoadAsync(key);
            }
            catch (Exception ex)
            {
                // 批量加载时跳过失败的键
                Console.WriteLine($"Failed to load {key}: {ex.Message}");
            }
        }
        
        return result;
    }

    private double GetChangeRateForKey(string key)
    {
        return key switch
        {
            var k when k.StartsWith("stock:") => 0.05,      // 股票价格变化5%
            var k when k.StartsWith("price:") => 0.03,      // 商品价格变化3%
            var k when k.StartsWith("sensor:") => 0.02,     // 传感器数据变化2%
            var k when k.StartsWith("metric:") => 0.10,     // 系统指标变化10%
            _ => 0.01                                        // 默认变化1%
        };
    }
}