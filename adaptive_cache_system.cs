using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Serilog;

// 熔断器状态枚举
public enum CircuitBreakerState
{
    Closed,    // 正常状态，允许请求通过
    Open,      // 熔断状态，拒绝请求
    HalfOpen   // 半开状态，允许少量请求测试服务是否恢复
}

// 熔断器配置
public class CircuitBreakerConfig
{
    public bool Enabled { get; set; } = true;
    public int FailureThreshold { get; set; } = 5;                    // 失败阈值
    public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromMinutes(1); // 熔断器打开后的超时时间
    public int HalfOpenMaxRequests { get; set; } = 3;                // 半开状态下允许的最大请求数
    public TimeSpan FailureWindow { get; set; } = TimeSpan.FromMinutes(5); // 失败统计窗口
    public double FailureRateThreshold { get; set; } = 0.5;          // 失败率阈值（50%）
    public int MinRequestsInWindow { get; set; } = 10;               // 窗口内最小请求数（用于计算失败率）
}

// 扩展配置类，添加熔断器配置
public class CacheConfig
{
    public LoadStrategy LoadStrategy { get; set; } = LoadStrategy.ResponseFirst;
    public CacheStrategy CacheStrategy { get; set; } = CacheStrategy.LRU;
    public int MaxCacheSize { get; set; } = 10000;
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(30);
    public int BatchSize { get; set; } = 100;
    public TimeSpan BatchWindow { get; set; } = TimeSpan.FromMilliseconds(50);
    public int MaxConcurrentBatches { get; set; } = 10;
    
    // 自动刷新配置
    public bool EnableAutoRefresh { get; set; } = false;
    public TimeSpan AutoRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);
    public double RefreshThreshold { get; set; } = 0.8; // 保留原有逻辑，但添加强制刷新
    public int MaxAutoRefreshBatchSize { get; set; } = 50;
    public AutoRefreshStrategy AutoRefreshStrategy { get; set; } = AutoRefreshStrategy.LeastRecentlyUsed;
    
    // 新增：强制刷新配置
    public bool EnableForceRefresh { get; set; } = false;            // 启用强制刷新（每个间隔必须刷新）
    public TimeSpan ForceRefreshInterval { get; set; } = TimeSpan.FromSeconds(10); // 强制刷新间隔
    
    // 自适应刷新配置
    public bool EnableAdaptiveRefresh { get; set; } = false;         // 启用自适应刷新
    public TimeSpan AdaptiveCheckInterval { get; set; } = TimeSpan.FromSeconds(10); // 自适应检查间隔
    
    // 熔断器配置
    public CircuitBreakerConfig CircuitBreaker { get; set; } = new CircuitBreakerConfig();
    
    // 降级策略配置
    public bool EnableStaleDataFallback { get; set; } = true;        // 启用过期数据降级
    public TimeSpan MaxStaleDataAge { get; set; } = TimeSpan.FromHours(1); // 最大过期数据年龄
}

// 自动刷新策略
public enum AutoRefreshStrategy
{
    All,
    LeastRecentlyUsed,
    MostFrequentlyUsed,
    OldestFirst,
    ExpirationBased,
    AdaptivePriority  // 新增：自适应优先级策略
}

// 加载策略枚举
public enum LoadStrategy
{
    ResponseFirst,
    FreshnessFirst
}

// 缓存策略枚举
public enum CacheStrategy
{
    LRU, LFU, FIFO, TTL
}

// 驱逐策略接口
public interface IEvictionStrategy<TKey, TValue>
{
    IEnumerable<TKey> SelectKeysForEviction(IDictionary<TKey, CacheItem<TValue>> items, int targetCount);
    CacheItemPriority GetPriority(CacheItem<TValue> item);
}

// 刷新策略接口
public interface IRefreshStrategy<TKey, TValue>
{
    IEnumerable<TKey> SelectKeysForRefresh(IDictionary<TKey, CacheItem<TValue>> items, int maxCount, CacheConfig config);
    bool ShouldRefresh(CacheItem<TValue> item, CacheConfig config);
}

// LRU驱逐策略
public class LRUEvictionStrategy<TKey, TValue> : IEvictionStrategy<TKey, TValue>
{
    public IEnumerable<TKey> SelectKeysForEviction(IDictionary<TKey, CacheItem<TValue>> items, int targetCount)
    {
        return items
            .OrderBy(kvp => kvp.Value.LastAccessedAt)
            .Take(targetCount)
            .Select(kvp => kvp.Key);
    }

    public CacheItemPriority GetPriority(CacheItem<TValue> item) => CacheItemPriority.Normal;
}

// LFU驱逐策略
public class LFUEvictionStrategy<TKey, TValue> : IEvictionStrategy<TKey, TValue>
{
    public IEnumerable<TKey> SelectKeysForEviction(IDictionary<TKey, CacheItem<TValue>> items, int targetCount)
    {
        return items
            .OrderBy(kvp => Interlocked.Read(ref kvp.Value.AccessCount))
            .ThenBy(kvp => kvp.Value.LastAccessedAt)
            .Take(targetCount)
            .Select(kvp => kvp.Key);
    }

    public CacheItemPriority GetPriority(CacheItem<TValue> item) => CacheItemPriority.High;
}

// FIFO驱逐策略
public class FIFOEvictionStrategy<TKey, TValue> : IEvictionStrategy<TKey, TValue>
{
    public IEnumerable<TKey> SelectKeysForEviction(IDictionary<TKey, CacheItem<TValue>> items, int targetCount)
    {
        return items
            .OrderBy(kvp => kvp.Value.CreatedAt)
            .Take(targetCount)
            .Select(kvp => kvp.Key);
    }

    public CacheItemPriority GetPriority(CacheItem<TValue> item) => CacheItemPriority.Low;
}

// TTL驱逐策略
public class TTLEvictionStrategy<TKey, TValue> : IEvictionStrategy<TKey, TValue>
{
    public IEnumerable<TKey> SelectKeysForEviction(IDictionary<TKey, CacheItem<TValue>> items, int targetCount)
    {
        return items
            .OrderBy(kvp => kvp.Value.ExpiresAt)
            .Take(targetCount)
            .Select(kvp => kvp.Key);
    }

    public CacheItemPriority GetPriority(CacheItem<TValue> item) => CacheItemPriority.NeverRemove;
}

// 默认刷新策略
public class DefaultRefreshStrategy<TKey, TValue> : IRefreshStrategy<TKey, TValue>
{
    public IEnumerable<TKey> SelectKeysForRefresh(IDictionary<TKey, CacheItem<TValue>> items, int maxCount, CacheConfig config)
    {
        var candidates = items.Values.Where(item => 
            !item.IsExpired && 
            item.IsAutoRefreshCandidate && 
            ShouldRefresh(item, config)).ToList();

        return config.AutoRefreshStrategy switch
        {
            AutoRefreshStrategy.All => candidates.Select(item => GetKeyFromItem(items, item)).ToList(),
            AutoRefreshStrategy.LeastRecentlyUsed => candidates
                .OrderBy(item => item.LastAccessedAt)
                .Take(maxCount)
                .Select(item => GetKeyFromItem(items, item)),
            AutoRefreshStrategy.MostFrequentlyUsed => candidates
                .OrderByDescending(item => Interlocked.Read(ref item.AccessCount))
                .Take(maxCount)
                .Select(item => GetKeyFromItem(items, item)),
            AutoRefreshStrategy.OldestFirst => candidates
                .OrderBy(item => item.CreatedAt)
                .Take(maxCount)
                .Select(item => GetKeyFromItem(items, item)),
            AutoRefreshStrategy.ExpirationBased => candidates
                .OrderBy(item => item.ExpiresAt)
                .Take(maxCount)
                .Select(item => GetKeyFromItem(items, item)),
            AutoRefreshStrategy.AdaptivePriority => candidates
                .Where(item => item.ShouldAdaptiveRefresh()) // 直接筛选需要刷新的项，无需排序
                .Take(maxCount)
                .Select(item => GetKeyFromItem(items, item)),
            _ => candidates.Take(maxCount).Select(item => GetKeyFromItem(items, item))
        };
    }

    public bool ShouldRefresh(CacheItem<TValue> item, CacheConfig config)
    {
        if (config.EnableForceRefresh)
        {
            return item.ShouldForceRefresh(config.ForceRefreshInterval);
        }
        
        if (config.EnableAdaptiveRefresh)
        {
            return item.ShouldAdaptiveRefresh();
        }
        
        return item.ShouldRefresh(config.RefreshThreshold);
    }

    private TKey GetKeyFromItem(IDictionary<TKey, CacheItem<TValue>> items, CacheItem<TValue> item)
    {
        return items.First(kvp => ReferenceEquals(kvp.Value, item)).Key;
    }
}

// 策略工厂接口
public interface IStrategyFactory<TKey, TValue>
{
    IEvictionStrategy<TKey, TValue> CreateEvictionStrategy(CacheStrategy strategy);
    IRefreshStrategy<TKey, TValue> CreateRefreshStrategy(AutoRefreshStrategy strategy);
}

// 策略工厂实现
public class CacheStrategyFactory<TKey, TValue> : IStrategyFactory<TKey, TValue>
{
    public IEvictionStrategy<TKey, TValue> CreateEvictionStrategy(CacheStrategy strategy)
    {
        return strategy switch
        {
            CacheStrategy.LRU => new LRUEvictionStrategy<TKey, TValue>(),
            CacheStrategy.LFU => new LFUEvictionStrategy<TKey, TValue>(),
            CacheStrategy.FIFO => new FIFOEvictionStrategy<TKey, TValue>(),
            CacheStrategy.TTL => new TTLEvictionStrategy<TKey, TValue>(),
            _ => new LRUEvictionStrategy<TKey, TValue>()
        };
    }

    public IRefreshStrategy<TKey, TValue> CreateRefreshStrategy(AutoRefreshStrategy strategy)
    {
        return new DefaultRefreshStrategy<TKey, TValue>();
    }
}

// 熔断器异常
public class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string message) : base(message) { }
}

// 请求记录（用于统计）
public class RequestRecord
{
    public DateTime Timestamp { get; set; }
    public bool IsSuccess { get; set; }
    public Exception Exception { get; set; }
}

// 熔断器实现
public class CircuitBreaker
{
    private readonly CircuitBreakerConfig _config;
    private readonly ILogger _logger;
    private volatile CircuitBreakerState _state = CircuitBreakerState.Closed;
    private readonly object _stateLock = new object();
    
    // 统计信息
    private readonly ConcurrentQueue<RequestRecord> _requestHistory = new();
    private long _lastFailureTimeTicks = DateTime.MinValue.Ticks;
    private long _lastStateChangeTimeTicks = DateTime.UtcNow.Ticks;
    private volatile int _halfOpenRequestCount = 0;
    
    // 统计计数器
    private long _totalRequests;
    private long _successfulRequests;
    private long _failedRequests;
    private long _circuitOpenRequests;

    public long TotalRequests => Interlocked.Read(ref _totalRequests);
    public long SuccessfulRequests => Interlocked.Read(ref _successfulRequests);
    public long FailedRequests => Interlocked.Read(ref _failedRequests);
    public long CircuitOpenRequests => Interlocked.Read(ref _circuitOpenRequests);

    public CircuitBreakerState State => _state; 
    public DateTime LastStateChangeTime => new DateTime(Interlocked.Read(ref _lastStateChangeTimeTicks), DateTimeKind.Utc);

    public CircuitBreaker(CircuitBreakerConfig config, ILogger logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    // 核心执行方法
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        if (!_config.Enabled)
        {
            return await operation();
        }

        CheckAndUpdateState();

        switch (_state)
        {
            case CircuitBreakerState.Open:
                Interlocked.Increment(ref _circuitOpenRequests);
                throw new CircuitBreakerOpenException("Circuit breaker is in open state");

            case CircuitBreakerState.HalfOpen:
                if (Interlocked.Increment(ref _halfOpenRequestCount) > _config.HalfOpenMaxRequests)
                {
                    Interlocked.Decrement(ref _halfOpenRequestCount);
                    Interlocked.Increment(ref _circuitOpenRequests);
                    throw new CircuitBreakerOpenException("Circuit breaker half-open request limit exceeded");
                }
                break;
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
            OnFailure(ex);
            throw;
        }
    }

    private void OnSuccess()
    {
        Interlocked.Increment(ref _successfulRequests);
        RecordRequest(true, null);

        if (_state == CircuitBreakerState.HalfOpen)
        {
            lock (_stateLock)
            {
                if (_state == CircuitBreakerState.HalfOpen)
                {
                    ChangeState(CircuitBreakerState.Closed);
                    _logger?.Information("Circuit breaker state changed to Closed after successful request");
                }
            }
        }
    }

    private void OnFailure(Exception exception)
    {
        Interlocked.Increment(ref _failedRequests);
        Interlocked.Exchange(ref _lastFailureTimeTicks, DateTime.UtcNow.Ticks);
        RecordRequest(false, exception);

        lock (_stateLock)
        {
            if (_state == CircuitBreakerState.HalfOpen)
            {
                ChangeState(CircuitBreakerState.Open);
                _logger?.Warning(exception, "Circuit breaker state changed to Open due to failure in half-open state");
            }
            else if (_state == CircuitBreakerState.Closed && ShouldTrip())
            {
                ChangeState(CircuitBreakerState.Open);
                _logger?.Warning(exception, "Circuit breaker state changed to Open due to failure threshold exceeded");
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
                    if (_state == CircuitBreakerState.Open && Interlocked.Read(ref _lastStateChangeTimeTicks) == lastChangeTicks)
                    {
                        ChangeState(CircuitBreakerState.HalfOpen);
                        _halfOpenRequestCount = 0;
                        _logger?.Information("Circuit breaker state changed to HalfOpen after timeout");
                    }
                }
            }
        }
    }

    private bool ShouldTrip()
    {
        CleanOldRecords();
        
        var recentRecords = _requestHistory.ToArray();
        if (recentRecords.Length < _config.MinRequestsInWindow)
        {
            return false;
        }

        var failureCount = recentRecords.Count(r => !r.IsSuccess);
        var failureRate = (double)failureCount / recentRecords.Length;

        return failureCount >= _config.FailureThreshold || failureRate >= _config.FailureRateThreshold;
    }

    private void RecordRequest(bool isSuccess, Exception exception)
    {
        _requestHistory.Enqueue(new RequestRecord
        {
            Timestamp = DateTime.UtcNow,
            IsSuccess = isSuccess,
            Exception = exception
        });

        CleanOldRecords();
    }

    private void CleanOldRecords()
    {
        var cutoffTime = DateTime.UtcNow - _config.FailureWindow;
        while (_requestHistory.TryPeek(out var record) && record.Timestamp < cutoffTime)
        {
            _requestHistory.TryDequeue(out _);
        }
    }

    private void ChangeState(CircuitBreakerState newState)
    {
        _state = newState;
        Interlocked.Exchange(ref _lastStateChangeTimeTicks, DateTime.UtcNow.Ticks);
    }

    public CircuitBreakerStatistics GetStatistics()
    {
        CleanOldRecords();
        var recentRecords = _requestHistory.ToArray();
        
        return new CircuitBreakerStatistics
        {
            State = _state,
            TotalRequests = this.TotalRequests,
            SuccessfulRequests = this.SuccessfulRequests,
            FailedRequests = this.FailedRequests,
            CircuitOpenRequests = this.CircuitOpenRequests,
            RecentFailureRate = recentRecords.Length > 0 ? 
                (double)recentRecords.Count(r => !r.IsSuccess) / recentRecords.Length : 0,
            LastStateChangeTime = this.LastStateChangeTime,
            LastFailureTime = new DateTime(Interlocked.Read(ref _lastFailureTimeTicks), DateTimeKind.Utc)
        };
    }

    public void Reset()
    {
        lock (_stateLock)
        {
            ChangeState(CircuitBreakerState.Closed);
            _halfOpenRequestCount = 0;
            while (_requestHistory.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _totalRequests, 0);
            Interlocked.Exchange(ref _successfulRequests, 0);
            Interlocked.Exchange(ref _failedRequests, 0);
            Interlocked.Exchange(ref _circuitOpenRequests, 0);
            _logger?.Information("Circuit breaker manually reset to Closed state");
        }
    }
}

// 熔断器统计信息
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

// 高效缓存项包装类（基于访问间隔的自适应刷新）
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
    
    // 简化的自适应刷新字段
    public DateTime LastDataChangeCheckTime { get; set; } = DateTime.MinValue;
    public TimeSpan EstimatedDataChangeInterval { get; set; } = TimeSpan.FromMinutes(15); // 预估数据变化间隔，默认15分钟
    
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    
    public bool ShouldRefresh(double threshold)
    {
        var totalLifetime = ExpiresAt - CreatedAt;
        var currentAge = DateTime.UtcNow - CreatedAt;
        return currentAge.TotalMilliseconds >= totalLifetime.TotalMilliseconds * threshold;
    }

    // 检查是否需要强制刷新
    public bool ShouldForceRefresh(TimeSpan forceRefreshInterval)
    {
        var lastRefresh = LastRefreshedAt == DateTime.MinValue ? CreatedAt : LastRefreshedAt;
        return DateTime.UtcNow - lastRefresh >= forceRefreshInterval;
    }
    
    // 基于数据变化间隔的自适应刷新判断 - 高效且简单
    public bool ShouldAdaptiveRefresh()
    {
        var now = DateTime.UtcNow;
        var lastCheck = LastDataChangeCheckTime == DateTime.MinValue ? CreatedAt : LastDataChangeCheckTime;
        
        // 如果距离上次检查的时间超过了预估的数据变化间隔，则需要刷新
        return (now - lastCheck) >= EstimatedDataChangeInterval;
    }
    
    // 更新数据变化间隔预估（基于实际访问模式调整）
    public void UpdateDataChangeInterval()
    {
        var now = DateTime.UtcNow;
        var accessFrequency = Interlocked.Read(ref AccessCount);
        var age = (now - CreatedAt).TotalMinutes;
        
        if (age > 0)
        {
            var accessesPerMinute = accessFrequency / age;
            
            // 根据访问频率动态调整数据变化检查间隔
            // 高频访问（>1次/分钟）: 5分钟检查一次
            // 中频访问（0.1-1次/分钟）: 15分钟检查一次  
            // 低频访问（<0.1次/分钟）: 30分钟检查一次
            EstimatedDataChangeInterval = accessesPerMinute switch
            {
                > 1.0 => TimeSpan.FromMinutes(5),
                > 0.1 => TimeSpan.FromMinutes(15),
                _ => TimeSpan.FromMinutes(30)
            };
        }
        
        LastDataChangeCheckTime = now;
    }
}

// 数据加载器接口
public interface IDataLoader<TKey, TValue>
{
    Task<TValue> LoadAsync(TKey key);
    Task<Dictionary<TKey, TValue>> LoadBatchAsync(IEnumerable<TKey> keys);
}

// 高性能内存缓存类（增强版：支持策略模式和自适应刷新）
public class AdvancedMemoryCache<TKey, TValue> : IDisposable
{
    private IMemoryCache _memoryCache;
    private readonly IDataLoader<TKey, TValue> _dataLoader;
    private readonly CacheConfig _config;
    private readonly Func<TKey, string> _keySerializer;
    private readonly ILogger _logger;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly IStrategyFactory<TKey, TValue> _strategyFactory;
    private readonly IEvictionStrategy<TKey, TValue> _evictionStrategy;
    private readonly IRefreshStrategy<TKey, TValue> _refreshStrategy;

    // 用于批量加载的并发控制
    private readonly ConcurrentDictionary<TKey, Task<TValue>> _loadingTasks = new();

    // 自动刷新相关
    private readonly Timer _autoRefreshTimer;
    private readonly Timer _adaptiveUpdateTimer; // 新增：自适应更新定时器
    private readonly ConcurrentDictionary<TKey, byte> _cacheKeys = new();
    private volatile bool _disposed = false;

    // 过期数据存储（用于降级）
    private readonly ConcurrentDictionary<TKey, CacheItem<TValue>> _staleDataStorage = new();

    // 统计信息
    private long _hitCount;
    private long _missCount;
    private long _loadCount;
    private long _autoRefreshCount;
    private long _forceRefreshCount;
    private long _adaptiveRefreshCount; // 新增：自适应刷新计数
    private long _staleDataHits;
    private long _circuitBreakerFallbacks;

    public long HitCount => Interlocked.Read(ref _hitCount);
    public long MissCount => Interlocked.Read(ref _missCount);
    public long LoadCount => Interlocked.Read(ref _loadCount);
    public long AutoRefreshCount => Interlocked.Read(ref _autoRefreshCount);
    public long ForceRefreshCount => Interlocked.Read(ref _forceRefreshCount);
    public long AdaptiveRefreshCount => Interlocked.Read(ref _adaptiveRefreshCount);
    public long StaleDataHits => Interlocked.Read(ref _staleDataHits);
    public long CircuitBreakerFallbacks => Interlocked.Read(ref _circuitBreakerFallbacks);

    public AdvancedMemoryCache(
        IDataLoader<TKey, TValue> dataLoader,
        IOptions<CacheConfig> config,
        ILogger logger = null,
        Func<TKey, string> keySerializer = null,
        IStrategyFactory<TKey, TValue> strategyFactory = null)
    {
        _dataLoader = dataLoader ?? throw new ArgumentNullException(nameof(dataLoader));
        _config = config?.Value ?? new CacheConfig();
        _logger = logger?.ForContext<AdvancedMemoryCache<TKey, TValue>>();
        _keySerializer = keySerializer ?? (key => key.ToString());
        _strategyFactory = strategyFactory ?? new CacheStrategyFactory<TKey, TValue>();

        _circuitBreaker = new CircuitBreaker(_config.CircuitBreaker, _logger);
        
        // 创建策略实例
        _evictionStrategy = _strategyFactory.CreateEvictionStrategy(_config.CacheStrategy);
        _refreshStrategy = _strategyFactory.CreateRefreshStrategy(_config.AutoRefreshStrategy);

        _memoryCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _config.MaxCacheSize
        });

        if (_config.EnableAutoRefresh)
        {
            var interval = _config.EnableForceRefresh ? _config.ForceRefreshInterval : _config.AutoRefreshInterval;
            _autoRefreshTimer = new Timer(AutoRefreshCallback, null, interval, Timeout.InfiniteTimeSpan);
        }
        
        // 启动自适应更新定时器（每10秒更新一次数据变化间隔预估）
        if (_config.EnableAdaptiveRefresh)
        {
            _adaptiveUpdateTimer = new Timer(AdaptiveUpdateCallback, null, 
                _config.AdaptiveCheckInterval, _config.AdaptiveCheckInterval);
        }
    }

    // 新增：高效的自适应更新定时器回调
    private void AdaptiveUpdateCallback(object state)
    {
        if (_disposed) return;

        _ = Task.Run(() =>
        {
            try
            {
                UpdateAllDataChangeIntervals();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Adaptive interval update failed");
            }
        });
    }

    // 新增：高效更新所有缓存项的数据变化间隔预估
    private void UpdateAllDataChangeIntervals()
    {
        var keysSnapshot = _cacheKeys.Keys.ToList();
        var updatedCount = 0;
        
        foreach (var key in keysSnapshot)
        {
            if (_disposed) break;
            
            var cacheKey = GetCacheKey(key);
            try
            {
                if (_memoryCache.TryGetValue(cacheKey, out CacheItem<TValue> item))
                {
                    item.UpdateDataChangeInterval();
                    updatedCount++;
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to update data change interval for key '{Key}'", key);
            }
        }
        
        _logger?.Debug("Updated data change intervals for {Count} cache items", updatedCount);
    }

    // 获取单个值
    public async Task<TValue> GetAsync(TKey key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));

        var cacheKey = GetCacheKey(key);

        if (_memoryCache.TryGetValue(cacheKey, out CacheItem<TValue> cachedItem))
        {
            if (!cachedItem.IsExpired)
            {
                UpdateAccessInfo(cachedItem);
                Interlocked.Increment(ref _hitCount);

                if (cachedItem.IsStale) Interlocked.Increment(ref _staleDataHits);

                // 使用刷新策略判断是否需要刷新
                if (_refreshStrategy.ShouldRefresh(cachedItem, _config) && _circuitBreaker.State == CircuitBreakerState.Closed)
                {
                    _ = RefreshCacheKeysDirectAsync(new[] { key }, countAsAutoRefresh: true);
                }
                
                return cachedItem.Value;
            }
            else
            {
                StoreAsStaleIfEligible(key, cachedItem);
                _memoryCache.Remove(cacheKey);
                _cacheKeys.TryRemove(key, out _);
            }
        }

        Interlocked.Increment(ref _missCount);

        try
        {
            return await LoadWithDeduplication(key); // 外部请求去重
        }
        catch (CircuitBreakerOpenException)
        {
            return await HandleCircuitBreakerFallback(key);
        }
    }

    // 熔断器降级处理
    private Task<TValue> HandleCircuitBreakerFallback(TKey key)
    {
        Interlocked.Increment(ref _circuitBreakerFallbacks);
        
        if (_config.EnableStaleDataFallback && _staleDataStorage.TryGetValue(key, out var staleItem))
        {
            var staleAge = DateTime.UtcNow - staleItem.CreatedAt;
            if (staleAge <= _config.MaxStaleDataAge)
            {
                _logger?.Warning("Using stale data for key '{Key}' due to circuit breaker open, age: {Age}", 
                    key, staleAge);
                
                Interlocked.Increment(ref _staleDataHits);
                return Task.FromResult(staleItem.Value);
            }
        }
        _logger?.Error("No fallback data available for key '{Key}' and circuit breaker is open", key);
        return Task.FromException<TValue>(new InvalidOperationException($"Circuit breaker is open and no fallback data available for key '{key}'"));
    }

    // 获取多个值
    public async Task<Dictionary<TKey, TValue>> GetManyAsync(IEnumerable<TKey> keys)
    {
        if (keys == null) throw new ArgumentNullException(nameof(keys));

        var result = new Dictionary<TKey, TValue>();
        var keysToLoad = new List<TKey>();
        var keysToRefresh = new List<TKey>();

        foreach (var key in keys)
        {
            var cacheKey = GetCacheKey(key);
            if (_memoryCache.TryGetValue(cacheKey, out CacheItem<TValue> cachedItem) && !cachedItem.IsExpired)
            {
                UpdateAccessInfo(cachedItem);
                result[key] = cachedItem.Value;
                Interlocked.Increment(ref _hitCount);

                if (cachedItem.IsStale) Interlocked.Increment(ref _staleDataHits);

                // 使用刷新策略判断是否需要刷新
                if (_refreshStrategy.ShouldRefresh(cachedItem, _config) && _circuitBreaker.State == CircuitBreakerState.Closed)
                {
                    keysToRefresh.Add(key);
                }
            }
            else
            {
                keysToLoad.Add(key);
                Interlocked.Increment(ref _missCount);
                if (cachedItem != null)
                {
                    StoreAsStaleIfEligible(key, cachedItem);
                    _memoryCache.Remove(cacheKey);
                    _cacheKeys.TryRemove(key, out _);
                }
            }
        }

        if (keysToLoad.Count > 0)
        {
            try
            {
                var loadedValues = await LoadBatchWithDeduplication(keysToLoad);
                foreach (var kvp in loadedValues) result[kvp.Key] = kvp.Value;
            }
            catch (CircuitBreakerOpenException)
            {
                foreach (var key in keysToLoad)
                {
                    try
                    {
                        result[key] = await HandleCircuitBreakerFallback(key);
                    }
                    catch { }
                }
            }
        }

        if (keysToRefresh.Count > 0 && _circuitBreaker.State == CircuitBreakerState.Closed)
        {
            _ = RefreshCacheKeysDirectAsync(keysToRefresh, countAsAutoRefresh: true);
        }

        return result;
    }

    private async Task RefreshCacheKeysDirectAsync(IEnumerable<TKey> keys, bool countAsAutoRefresh = false)
    {
        var batchSize = _config.MaxAutoRefreshBatchSize;
        var batches = keys
            .Select((k, i) => new { k, i })
            .GroupBy(x => x.i / batchSize)
            .Select(g => g.Select(x => x.k).ToList());

        foreach (var batch in batches)
        {
            try
            {
                var loadedData = await _circuitBreaker.ExecuteAsync(() => _dataLoader.LoadBatchAsync(batch));
                foreach (var kv in loadedData)
                {
                    SetCache(kv.Key, kv.Value, isRefresh: true);
                }

                // 根据刷新类型增加相应计数
                if (_config.EnableForceRefresh)
                    Interlocked.Add(ref _forceRefreshCount, batch.Count);
                else if (_config.EnableAdaptiveRefresh && countAsAutoRefresh)
                    Interlocked.Add(ref _adaptiveRefreshCount, batch.Count);
                else if (countAsAutoRefresh)
                    Interlocked.Add(ref _autoRefreshCount, batch.Count);

                _logger?.Debug("Directly refreshed {Count} keys", batch.Count);
            }
            catch (CircuitBreakerOpenException)
            {
                _logger?.Warning("Direct refresh cancelled due to circuit breaker open");
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "Direct refresh batch failed");
            }
        }
    }

    private void StoreAsStaleIfEligible(TKey key, CacheItem<TValue> item)
    {
        if (_config.EnableStaleDataFallback)
        {
            var staleAge = DateTime.UtcNow - item.CreatedAt;
            if (staleAge <= _config.MaxStaleDataAge)
            {
                item.IsStale = true;
                _staleDataStorage[key] = item;
                _logger?.Debug("Stored expired item for key '{Key}' as stale data. Age: {Age}", 
                    key, staleAge);
            }
        }
    }

    private Task RefreshKeysAsync(IEnumerable<TKey> keys, bool countAsAutoRefresh = false)
    {
         return RefreshCacheKeysDirectAsync(keys, countAsAutoRefresh);
    }

    // 修改后的自动刷新回调方法
    private void AutoRefreshCallback(object state)
    {
        if (_disposed || _circuitBreaker.State == CircuitBreakerState.Open) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var keysToRefresh = GetKeysForAutoRefresh();
                if (keysToRefresh.Any())
                {
                    foreach (var batch in keysToRefresh
                        .Select((k, i) => new { k, i })
                        .GroupBy(x => x.i / _config.MaxAutoRefreshBatchSize)
                        .Select(g => g.Select(x => x.k).ToList()))
                    {
                        await RefreshKeysAsync(batch, countAsAutoRefresh: true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Auto refresh failed");
            }
            finally
            {
                var interval = _config.EnableForceRefresh ? _config.ForceRefreshInterval : _config.AutoRefreshInterval;
                _autoRefreshTimer?.Change(interval, Timeout.InfiniteTimeSpan);
            }
        });
    }

    // 修改后的获取需要自动刷新的键方法（使用策略）
    private List<TKey> GetKeysForAutoRefresh()
    {
        var cacheItems = new Dictionary<TKey, CacheItem<TValue>>();
        
        foreach (var key in _cacheKeys.Keys.ToList())
        {
            var cacheKey = GetCacheKey(key);
            try
            {
                if (_memoryCache.TryGetValue(cacheKey, out CacheItem<TValue> item))
                {
                    if (!item.IsExpired && item.IsAutoRefreshCandidate)
                    {
                        cacheItems[key] = item;
                    }
                }
                else
                {
                    _cacheKeys.TryRemove(key, out _);
                }
            }
            catch (ObjectDisposedException)
            {
                _logger?.Warning("Cache was disposed during auto-refresh key scan. Aborting.");
                return new List<TKey>();
            }
        }

        // 使用刷新策略选择键
        var selectedKeys = _refreshStrategy.SelectKeysForRefresh(cacheItems, _config.MaxAutoRefreshBatchSize, _config).ToList();
        
        _logger?.Debug("Refresh strategy selected {Count} keys from {Total} candidates", 
            selectedKeys.Count, cacheItems.Count);
            
        return selectedKeys;
    }

    // 批量后台刷新
    private async Task RefreshBatchInBackground(List<TKey> keys)
    {
        try
        {
            var loadedData = await _circuitBreaker.ExecuteAsync(() => _dataLoader.LoadBatchAsync(keys));
            
            foreach (var kvp in loadedData)
            {
                SetCache(kvp.Key, kvp.Value, isRefresh: true); // 标记为刷新操作
            }
            
            _logger?.Debug("Successfully refreshed {Count} keys in background", loadedData.Count);
        }
        catch (CircuitBreakerOpenException)
        {
            _logger?.Warning("Batch background refresh cancelled due to circuit breaker open");
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "Batch background refresh failed for {Count} keys", keys.Count);
            throw;
        }
    }

    // 手动触发自动刷新
    public async Task TriggerAutoRefreshAsync()
    {
        if (_config.EnableAutoRefresh)
        {
            await PerformAutoRefresh();
        }
        else
        {
            throw new InvalidOperationException("Auto refresh is disabled");
        }
    }

    // 修改后的执行自动刷新方法
    private async Task PerformAutoRefresh()
    {
        if (_circuitBreaker.State == CircuitBreakerState.Open)
        {
            _logger?.Debug("Skipping auto refresh due to circuit breaker open state");
            return;
        }

        var keysToRefresh = GetKeysForAutoRefresh();
        if (!keysToRefresh.Any())
        {
            _logger?.Debug("No keys need auto refresh");
            return;
        }

        var refreshType = _config.EnableForceRefresh ? "Force" : 
                         _config.EnableAdaptiveRefresh ? "Adaptive" : "Standard";
        
        _logger?.Information("Starting {RefreshType} refresh for {Count} keys", 
            refreshType, keysToRefresh.Count);
        
        var batches = keysToRefresh
            .Select((key, index) => new { key, index })
            .GroupBy(x => x.index / _config.MaxAutoRefreshBatchSize)
            .Select(g => g.Select(x => x.key).ToList())
            .ToList();

        foreach (var batch in batches)
        {
            if (_disposed || _circuitBreaker.State == CircuitBreakerState.Open) break;
            
            try
            {
                await RefreshBatchInBackground(batch);
                
                // 根据刷新类型增加相应计数
                if (_config.EnableForceRefresh)
                {
                    Interlocked.Add(ref _forceRefreshCount, batch.Count);
                }
                else if (_config.EnableAdaptiveRefresh)
                {
                    Interlocked.Add(ref _adaptiveRefreshCount, batch.Count);
                }
                else
                {
                    Interlocked.Add(ref _autoRefreshCount, batch.Count);
                }
            }
            catch (CircuitBreakerOpenException)
            {
                _logger?.Warning("Auto refresh batch cancelled due to circuit breaker open");
                break;
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "Failed to refresh batch of {Count} keys", batch.Count);
            }
        }

        _logger?.Information("{RefreshType} refresh completed", refreshType);
    }

    // 设置指定键是否参与自动刷新
    public void SetAutoRefreshEnabled(TKey key, bool enabled)
    {
        var cacheKey = GetCacheKey(key);
        if (_memoryCache.TryGetValue(cacheKey, out CacheItem<TValue> item))
        {
            item.IsAutoRefreshCandidate = enabled;
        }
    }

    // 带去重的单个加载
    private async Task<TValue> LoadWithDeduplication(TKey key)
    {
        var result = await LoadBatchWithDeduplication(new[] { key });
        if (result.TryGetValue(key, out var value))
            return value;
        throw new KeyNotFoundException($"No value for key '{key}'");
    }
    
    // 带去重的批量加载
    private async Task<Dictionary<TKey, TValue>> LoadBatchWithDeduplication(ICollection<TKey> keys, bool isRefresh = false)
    {
        if (keys == null || !keys.Any())
            return new Dictionary<TKey, TValue>();

        var tasksToAwait = new Dictionary<TKey, Task<TValue>>();
        var keysToLoad = new List<TKey>();
        var tcsMap = new Dictionary<TKey, TaskCompletionSource<TValue>>();

        foreach (var key in keys.Distinct())
        {
            var tcs = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
            var existingTask = _loadingTasks.GetOrAdd(key, tcs.Task);
            tasksToAwait[key] = existingTask;

            if (existingTask == tcs.Task)
            {
                keysToLoad.Add(key);
                tcsMap[key] = tcs;
            }
        }

        if (keysToLoad.Count > 0)
        {
            try
            {
                var loadedData = await _circuitBreaker.ExecuteAsync(() => _dataLoader.LoadBatchAsync(keysToLoad));
                Interlocked.Add(ref _loadCount, keysToLoad.Count);

                foreach (var kvp in tcsMap)
                {
                    if (loadedData.TryGetValue(kvp.Key, out var value))
                    {
                        SetCache(kvp.Key, value, isRefresh);
                        kvp.Value.SetResult(value);
                    }
                    else
                    {
                        kvp.Value.SetException(new KeyNotFoundException($"Key '{kvp.Key}' was not found."));
                    }
                    _loadingTasks.TryRemove(kvp.Key, out _);
                }
            }
            catch (Exception ex)
            {
                foreach (var kvp in tcsMap)
                {
                    kvp.Value.SetException(ex);
                    _loadingTasks.TryRemove(kvp.Key, out _);
                }
                throw;
            }
        }

        await Task.WhenAll(tasksToAwait.Values);

        var finalResult = new Dictionary<TKey, TValue>();
        foreach (var kvp in tasksToAwait)
        {
            if (kvp.Value.Status == TaskStatus.RanToCompletion)
                finalResult[kvp.Key] = kvp.Value.Result;
        }
        return finalResult;
    }

    // 后台刷新
    private async Task RefreshInBackground(TKey key)
    {
        try
        {
            var value = await _circuitBreaker.ExecuteAsync(() => _dataLoader.LoadAsync(key));
            SetCache(key, value, isRefresh: true); // 标记为刷新操作
        }
        catch (CircuitBreakerOpenException)
        {
            _logger?.Debug("Background refresh for key '{Key}' cancelled due to circuit breaker open", key);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "Background refresh for key '{Key}' failed", key);
        }
    }

    // 修改后的设置缓存方法，支持刷新标记和自适应得分重置
    private void SetCache(TKey key, TValue value, bool isRefresh = false)
    {
        var cacheKey = GetCacheKey(key);
        var now = DateTime.UtcNow;

        // 默认创建一个新的缓存项
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

        // 如果是刷新操作，并且缓存中已存在旧项，则保留其历史访问信息
        if (isRefresh && _memoryCache.TryGetValue(cacheKey, out CacheItem<TValue> existingItem))
        {
            cacheItem.CreatedAt = existingItem.CreatedAt;
            cacheItem.LastAccessedAt = existingItem.LastAccessedAt;
            // 读取旧的访问计数值，而不是直接赋值，以保证线程安全
            cacheItem.AccessCount = Interlocked.Read(ref existingItem.AccessCount);
            cacheItem.IsAutoRefreshCandidate = existingItem.IsAutoRefreshCandidate;
            
            // 如果启用自适应刷新，重置数据变化检查时间
            if (_config.EnableAdaptiveRefresh)
            {
                cacheItem.LastDataChangeCheckTime = now;
            }
        }
        
        // 使用驱逐策略获取优先级
        var priority = _config.EnableForceRefresh 
            ? CacheItemPriority.NeverRemove 
            : _evictionStrategy.GetPriority(cacheItem);

        var entryOptions = new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = _config.DefaultExpiration,
            Priority = priority
        };

        // 添加移除回调，用于清理键跟踪
        entryOptions.RegisterPostEvictionCallback((k, v, reason, state) =>
        {
            // 当一个缓存项因为被"替换"(Replaced)而触发回调时，我们不应该将其从键跟踪中移除。
            if (reason == EvictionReason.Replaced)
            {
                return;
            }

            _cacheKeys.TryRemove(key, out _);
            
            // 如果启用了过期数据降级，将被移除的项保存到过期数据存储
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
            _logger?.Warning("Attempted to set key '{Key}' on a disposed cache. Operation skipped.", key);
            return;
        }
        
        // 清理过期的降级数据
        CleanStaleData();
    }

    // 清理过期的降级数据
    private void CleanStaleData()
    {
        var cutoffTime = DateTime.UtcNow - _config.MaxStaleDataAge;
        var keysToRemove = new List<TKey>();
        
        foreach (var kvp in _staleDataStorage)
        {
            if (kvp.Value.CreatedAt < cutoffTime)
            {
                keysToRemove.Add(kvp.Key);
            }
        }
        
        foreach (var key in keysToRemove)
        {
            _staleDataStorage.TryRemove(key, out _);
        }
    }

    // 更新访问信息（包含自适应得分更新）
    private void UpdateAccessInfo(CacheItem<TValue> item)
    {
        item.LastAccessedAt = DateTime.UtcNow;
        Interlocked.Increment(ref item.AccessCount);
        
        // 更新访问信息（包含数据变化间隔更新）
        if (_config.EnableAdaptiveRefresh)
        {
            item.UpdateDataChangeInterval();
        }
    }

    // 获取缓存键
    private string GetCacheKey(TKey key)
    {
        return $"cache:{typeof(TValue).Name}:{_keySerializer(key)}";
    }

    // 获取缓存统计信息
    public CacheStatistics GetStatistics()
    {
        var total = HitCount + MissCount;
        return new CacheStatistics
        {
            HitCount = HitCount,
            MissCount = MissCount,
            LoadCount = LoadCount,
            AutoRefreshCount = AutoRefreshCount,
            ForceRefreshCount = ForceRefreshCount,
            AdaptiveRefreshCount = AdaptiveRefreshCount, // 新增
            StaleDataHits = StaleDataHits,
            CircuitBreakerFallbacks = CircuitBreakerFallbacks,
            HitRatio = total > 0 ? (double)HitCount / total : 0,
            TotalRequests = total,
            CachedItemsCount = _cacheKeys.Count,
            StaleDataCount = _staleDataStorage.Count,
            CircuitBreakerStats = _circuitBreaker.GetStatistics()
        };
    }

    // 获取熔断器统计信息
    public CircuitBreakerStatistics GetCircuitBreakerStatistics()
    {
        return _circuitBreaker.GetStatistics();
    }

    // 手动重置熔断器
    public void ResetCircuitBreaker()
    {
        _circuitBreaker.Reset();
        _logger?.Information("Circuit breaker manually reset");
    }

    // 获取数据变化间隔信息（替换原来的自适应得分方法）
    public Dictionary<TKey, TimeSpan> GetDataChangeIntervals()
    {
        var result = new Dictionary<TKey, TimeSpan>();
        
        foreach (var key in _cacheKeys.Keys.ToList())
        {
            var cacheKey = GetCacheKey(key);
            try
            {
                if (_memoryCache.TryGetValue(cacheKey, out CacheItem<TValue> item))
                {
                    result[key] = item.EstimatedDataChangeInterval;
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
        
        return result;
    }

    // 清空缓存
    public void Clear()
    {
        var oldCache = _memoryCache;
        var memoryCacheOptions = new MemoryCacheOptions()
        {
            SizeLimit = _config.MaxCacheSize
        };
        _memoryCache = new MemoryCache(memoryCacheOptions);
        oldCache?.Dispose();

        _loadingTasks.Clear();
        _cacheKeys.Clear();
        _staleDataStorage.Clear();
        
        // 重置统计
        Interlocked.Exchange(ref _hitCount, 0);
        Interlocked.Exchange(ref _missCount, 0);
        Interlocked.Exchange(ref _loadCount, 0);
        Interlocked.Exchange(ref _autoRefreshCount, 0);
        Interlocked.Exchange(ref _forceRefreshCount, 0);
        Interlocked.Exchange(ref _adaptiveRefreshCount, 0);
        Interlocked.Exchange(ref _staleDataHits, 0);
        Interlocked.Exchange(ref _circuitBreakerFallbacks, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _autoRefreshTimer?.Dispose();
        _adaptiveUpdateTimer?.Dispose(); // 新增：释放自适应定时器
        _memoryCache?.Dispose();
        
        _loadingTasks.Clear();
        _cacheKeys.Clear();
        _staleDataStorage.Clear();
    }
}

// 扩展缓存统计信息
public class CacheStatistics
{
    public long HitCount { get; set; }
    public long MissCount { get; set; }
    public long LoadCount { get; set; }
    public long AutoRefreshCount { get; set; }
    public long ForceRefreshCount { get; set; }
    public long AdaptiveRefreshCount { get; set; } // 新增：自适应刷新计数
    public long StaleDataHits { get; set; }
    public long CircuitBreakerFallbacks { get; set; }
    public double HitRatio { get; set; }
    public long TotalRequests { get; set; }
    public int CachedItemsCount { get; set; }
    public int StaleDataCount { get; set; }
    public CircuitBreakerStatistics CircuitBreakerStats { get; set; }
}

// 示例数据加载器实现（带故障模拟）
public class ExampleDataLoader : IDataLoader<string, string>
{
    private static int _callCount = 0;
    private readonly Random _random = new Random();

    public async Task<string> LoadAsync(string key)
    {
        Interlocked.Increment(ref _callCount);
        
        // 模拟间歇性故障（每5次调用失败一次）
        if (_callCount % 5 == 0)
        {
            throw new InvalidOperationException($"Simulated failure for key: {key}");
        }
        
        // 模拟网络延迟
        await Task.Delay(_random.Next(50, 200));
        return $"Value for {key} loaded at {DateTime.Now}";
    }

    public async Task<Dictionary<string, string>> LoadBatchAsync(IEnumerable<string> keys)
    {
        var keyList = keys.ToList();
        Interlocked.Add(ref _callCount, keyList.Count);
        
        // 模拟批量调用的故障
        if (_callCount % 7 == 0)
        {
            throw new TimeoutException("Simulated batch timeout");
        }
        
        // 模拟批量网络延迟
        await Task.Delay(_random.Next(100, 400));
        
        var result = new Dictionary<string, string>();
        foreach (var key in keyList)
        {
            result[key] = $"Batch value for {key} loaded at {DateTime.Now}";
        }
        return result;
    }
}

// 使用示例 - 高效自适应刷新演示
public class EfficientAdaptiveCacheUsageExample
{
    public static async Task Example()
    {
        // 配置缓存，启用高效自适应刷新
        var config = new CacheConfig
        {
            LoadStrategy = LoadStrategy.ResponseFirst,
            CacheStrategy = CacheStrategy.LRU,
            MaxCacheSize = 1000,
            DefaultExpiration = TimeSpan.FromMinutes(30), // 缓存30分钟过期
            
            // 启用高效自适应刷新配置
            EnableAutoRefresh = true,
            EnableAdaptiveRefresh = true,              // 启用自适应刷新
            AutoRefreshInterval = TimeSpan.FromSeconds(15), // 每15秒检查一次
            MaxAutoRefreshBatchSize = 100,
            AutoRefreshStrategy = AutoRefreshStrategy.AdaptivePriority, // 使用自适应优先级策略
            
            // 简化的自适应参数配置
            AdaptiveCheckInterval = TimeSpan.FromSeconds(10), // 每10秒更新数据变化间隔预估
            
            // 熔断器配置
            CircuitBreaker = new CircuitBreakerConfig
            {
                Enabled = true,
                FailureThreshold = 3,
                OpenTimeout = TimeSpan.FromSeconds(30),
                HalfOpenMaxRequests = 2,
                FailureWindow = TimeSpan.FromMinutes(2),
                FailureRateThreshold = 0.6,
                MinRequestsInWindow = 5
            },
            
            // 降级配置
            EnableStaleDataFallback = true,
            MaxStaleDataAge = TimeSpan.FromHours(1)
        };

        var dataLoader = new ExampleDataLoader();
        
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();
        
        var cache = new AdvancedMemoryCache<string, string>(
            dataLoader, Options.Create(config), logger);

        try
        {
            Console.WriteLine("=== 高效自适应刷新缓存演示 ===");

            // 预加载一些数据
            Console.WriteLine("Preloading data...");
            var initialKeys = Enumerable.Range(1, 10).Select(i => $"key{i}").ToList();
            try
            {
                var initialValues = await cache.GetManyAsync(initialKeys);
                foreach (var kvp in initialValues)
                {
                    Console.WriteLine($"Loaded: {kvp.Key} = {kvp.Value.Substring(0, Math.Min(30, kvp.Value.Length))}...");
                }
            }
            catch (Exception ex) 
            { 
                Console.WriteLine($"Failed to preload data: {ex.Message}"); 
            }
            
            Console.WriteLine("\n=== 模拟不同访问模式以触发自适应刷新 ===");
            
            // 模拟访问模式：
            // - key1, key2: 高频访问（每轮访问5次）-> 应该得到5分钟的数据变化检查间隔
            // - key3, key4: 中频访问（每2轮访问1次）-> 应该得到15分钟的间隔  
            // - key5-key10: 低频访问（每5轮访问1次）-> 应该得到30分钟的间隔
            
            for (int round = 0; round < 15; round++) // 运行15轮，每轮4秒
            {
                Console.WriteLine($"\n--- 第 {round + 1} 轮访问 ---");
                
                // 高频访问 key1, key2（每轮访问5次）
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        await cache.GetAsync("key1");
                        await cache.GetAsync("key2");
                    }
                    catch { }
                    await Task.Delay(100); // 100ms间隔
                }
                
                // 中频访问 key3, key4（每2轮访问1次）  
                if (round % 2 == 0)
                {
                    try
                    {
                        await cache.GetAsync("key3");
                        await cache.GetAsync("key4");
                    }
                    catch { }
                }
                
                // 低频访问 key5-key10（每5轮访问1次）
                if (round % 5 == 0)
                {
                    try
                    {
                        for (int i = 5; i <= 10; i++)
                        {
                            await cache.GetAsync($"key{i}");
                        }
                    }
                    catch { }
                }
                
                // 获取并显示统计信息和数据变化间隔
                var stats = cache.GetStatistics();
                var dataChangeIntervals = cache.GetDataChangeIntervals();
                var cbStats = stats.CircuitBreakerStats;
                
                Console.WriteLine($"统计 ({DateTime.Now:HH:mm:ss}) - Hit: {stats.HitCount}, Miss: {stats.MissCount}, Hit Ratio: {stats.HitRatio:P2}");
                Console.WriteLine($"刷新 - Auto: {stats.AutoRefreshCount}, Adaptive: {stats.AdaptiveRefreshCount} ← 观察自适应刷新");
                Console.WriteLine($"熔断器 - State: {cbStats.State}, Failed: {cbStats.FailedRequests}/{cbStats.TotalRequests}");
                
                // 显示数据变化间隔（按间隔排序，无需复杂计算）
                var intervalGroups = dataChangeIntervals
                    .GroupBy(kvp => kvp.Value.TotalMinutes)
                    .OrderBy(g => g.Key)
                    .ToList();
                    
                if (intervalGroups.Any())
                {
                    Console.WriteLine("数据变化检查间隔分组:");
                    foreach (var group in intervalGroups)
                    {
                        var keys = string.Join(", ", group.Select(kvp => kvp.Key));
                        var intervalDescription = group.Key switch
                        {
                            5 => "5分钟(高频)",
                            15 => "15分钟(中频)",
                            30 => "30分钟(低频)",
                            _ => $"{group.Key}分钟"
                        };
                        Console.WriteLine($"  {intervalDescription}: {keys}");
                    }
                }
                
                await Task.Delay(2500); // 等待2.5秒进入下一轮
            }
            
            // 最终分析
            Console.WriteLine("\n=== 最终分析 ===");
            var finalStats = cache.GetStatistics();
            var finalIntervals = cache.GetDataChangeIntervals();
            
            Console.WriteLine($"总请求: {finalStats.TotalRequests}");
            Console.WriteLine($"缓存命中率: {finalStats.HitRatio:P2}");
            Console.WriteLine($"标准自动刷新次数: {finalStats.AutoRefreshCount}");
            Console.WriteLine($"自适应刷新次数: {finalStats.AdaptiveRefreshCount} ← 应该主要刷新高频访问的键");
            Console.WriteLine($"总加载次数: {finalStats.LoadCount}");
            
            Console.WriteLine("\n所有键的最终数据变化检查间隔:");
            foreach (var group in finalIntervals.GroupBy(kvp => kvp.Value.TotalMinutes).OrderBy(g => g.Key))
            {
                var keys = string.Join(", ", group.Select(kvp => kvp.Key));
                var description = group.Key switch
                {
                    5 => "高优先级(5分钟)",
                    15 => "中优先级(15分钟)", 
                    30 => "低优先级(30分钟)",
                    _ => $"{group.Key}分钟"
                };
                Console.WriteLine($"  {description}: {keys}");
            }
            
            Console.WriteLine("\n预期结果:");
            Console.WriteLine("- key1, key2 应该有5分钟间隔（高频访问）");
            Console.WriteLine("- key3, key4 应该有15分钟间隔（中频访问）");  
            Console.WriteLine("- key5-key10 应该有30分钟间隔（低频访问）");
            Console.WriteLine("- AdaptiveRefreshCount 应该 > 0，且主要刷新高频键");
            Console.WriteLine("- 无需复杂排序，性能更佳");
            
        }
        finally
        {
            cache.Dispose();
            (logger as IDisposable)?.Dispose();
            Console.WriteLine("\n缓存已释放");
        }
    }
}.WriteLine($"Failed to preload data: {ex.Message}"); 
            }
            
            Console.WriteLine("\n=== 模拟不同访问模式以触发自适应刷新 ===");
            
            // 模拟访问模式：
            // - key1, key2: 高频访问
            // - key3, key4: 中频访问  
            // - key5-key10: 低频访问
            
            for (int round = 0; round < 20; round++) // 运行20轮，每轮5秒
            {
                Console.WriteLine($"\n--- 第 {round + 1} 轮访问 ---");
                
                // 高频访问 key1, key2（每轮访问5次）
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        await cache.GetAsync("key1");
                        await cache.GetAsync("key2");
                    }
                    catch { }
                    await Task.Delay(200); // 200ms间隔
                }
                
                // 中频访问 key3, key4（每轮访问2次）  
                if (round % 2 == 0)
                {
                    try
                    {
                        await cache.GetAsync("key3");
                        await cache.GetAsync("key4");
                    }
                    catch { }
                }
                
                // 低频访问 key5-key10（每5轮访问1次）
                if (round % 5 == 0)
                {
                    try
                    {
                        for (int i = 5; i <= 10; i++)
                        {
                            await cache.GetAsync($"key{i}");
                        }
                    }
                    catch { }
                }
                
                // 获取并显示统计信息和自适应得分
                var stats = cache.GetStatistics();
                var adaptiveScores = cache.GetAdaptiveScores();
                var cbStats = stats.CircuitBreakerStats;
                
                Console.WriteLine($"统计 ({DateTime.Now:HH:mm:ss}) - Hit: {stats.HitCount}, Miss: {stats.MissCount}, Hit Ratio: {stats.HitRatio:P2}");
                Console.WriteLine($"刷新 - Auto: {stats.AutoRefreshCount}, Adaptive: {stats.AdaptiveRefreshCount} ← 观察自适应刷新");
                Console.WriteLine($"熔断器 - State: {cbStats.State}, Failed: {cbStats.FailedRequests}/{cbStats.TotalRequests}");
                
                // 显示前5个键的自适应得分（按得分降序排列）
                var topScores = adaptiveScores
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(5)
                    .ToList();
                    
                if (topScores.Any())
                {
                    Console.WriteLine("Top 5 自适应得分:");
                    foreach (var kvp in topScores)
                    {
                        Console.WriteLine($"  {kvp.Key}: {kvp.Value:F3} {(kvp.Value >= config.AdaptiveThreshold ? "← 达到刷新阈值" : "")}");
                    }
                }
                
                await Task.Delay(3000); // 等待3秒进入下一轮
            }
            
            // 最终分析
            Console.WriteLine("\n=== 最终分析 ===");
            var finalStats = cache.GetStatistics();
            var finalScores = cache.GetAdaptiveScores();
            
            Console.WriteLine($"总请求: {finalStats.TotalRequests}");
            Console.WriteLine($"缓存命中率: {finalStats.HitRatio:P2}");
            Console.WriteLine($"标准自动刷新次数: {finalStats.AutoRefreshCount}");
            Console.WriteLine($"自适应刷新次数: {finalStats.AdaptiveRefreshCount} ← 应该主要刷新高频访问的键");
            Console.WriteLine($"总加载次数: {finalStats.LoadCount}");
            
            Console.WriteLine("\n所有键的最终自适应得分:");
            foreach (var kvp in finalScores.OrderByDescending(x => x.Value))
            {
                var description = kvp.Value switch
                {
                    >= 0.5 => "高优先级",
                    >= 0.2 => "中优先级", 
                    _ => "低优先级"
                };
                Console.WriteLine($"  {kvp.Key}: {kvp.Value:F3} ({description})");
            }
            
            Console.WriteLine("\n预期结果:");
            Console.WriteLine("- key1, key2 应该有最高的自适应得分（高频访问）");
            Console.WriteLine("- key3, key4 应该有中等的自适应得分（中频访问）");  
            Console.WriteLine("- key5-key10 应该有较低的自适应得分（低频访问）");
            Console.WriteLine("- AdaptiveRefreshCount 应该 > 0，说明自适应刷新在工作");
            
        }
        finally
        {
            cache.Dispose();
            (logger as IDisposable)?.Dispose();
            Console.WriteLine("\n缓存已释放");
        }
    }
}

// 综合演示示例 - 展示所有功能
public class ComprehensiveCacheUsageExample  
{
    public static async Task Example()
    {
        Console.WriteLine("=== 综合缓存功能演示 ===");
        
        // 配置支持所有功能的缓存
        var config = new CacheConfig
        {
            LoadStrategy = LoadStrategy.ResponseFirst,
            CacheStrategy = CacheStrategy.LFU, // 使用LFU驱逐策略
            MaxCacheSize = 100,
            DefaultExpiration = TimeSpan.FromMinutes(5),
            
            // 启用所有刷新功能
            EnableAutoRefresh = true,
            EnableForceRefresh = false,        // 不启用强制刷新，使用自适应
            EnableAdaptiveRefresh = true,      // 启用自适应刷新
            AutoRefreshInterval = TimeSpan.FromSeconds(10),
            MaxAutoRefreshBatchSize = 20,
            AutoRefreshStrategy = AutoRefreshStrategy.AdaptivePriority,
            
            // 自适应参数
            AdaptiveAlpha = 0.4,
            AdaptiveThreshold = 0.15,
            AdaptiveMinSamples = 2,
            
            // 熔断器配置
            CircuitBreaker = new CircuitBreakerConfig
            {
                Enabled = true,
                FailureThreshold = 2,
                OpenTimeout = TimeSpan.FromSeconds(15),
                HalfOpenMaxRequests = 1,
                FailureWindow = TimeSpan.FromMinutes(1),
                FailureRateThreshold = 0.5,
                MinRequestsInWindow = 3
            },
            
            // 降级配置
            EnableStaleDataFallback = true,
            MaxStaleDataAge = TimeSpan.FromMinutes(30)
        };

        var dataLoader = new ExampleDataLoader();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        
        // 可以自定义策略工厂
        var strategyFactory = new CacheStrategyFactory<string, string>();
        
        var cache = new AdvancedMemoryCache<string, string>(
            dataLoader, Options.Create(config), logger, null, strategyFactory);

        try
        {
            Console.WriteLine("\n1. 批量预加载数据...");
            var keys = Enumerable.Range(1, 15).Select(i => $"item{i}").ToArray();
            var initialData = await cache.GetManyAsync(keys);
            Console.WriteLine($"成功加载 {initialData.Count} 项数据");
            
            Console.WriteLine("\n2. 模拟不同访问模式...");
            
            // 模拟30秒的运行，观察各种功能
            var endTime = DateTime.UtcNow.AddSeconds(30);
            var iteration = 0;
            
            while (DateTime.UtcNow < endTime)
            {
                iteration++;
                Console.WriteLine($"\n--- 迭代 {iteration} ---");
                
                // 高频访问某些键
                var highFreqKeys = new[] { "item1", "item2", "item3" };
                foreach (var key in highFreqKeys)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        try { await cache.GetAsync(key); } catch { }
                        await Task.Delay(100);
                    }
                }
                
                // 中频访问
                if (iteration % 2 == 0)
                {
                    try { await cache.GetAsync("item4"); } catch { }
                    try { await cache.GetAsync("item5"); } catch { }
                }
                
                // 低频访问
                if (iteration % 4 == 0)
                {
                    var lowFreqKeys = new[] { "item6", "item7", "item8", "item9", "item10" };
                    try { await cache.GetManyAsync(lowFreqKeys); } catch { }
                }
                
                // 显示统计信息
                var stats = cache.GetStatistics();
                var scores = cache.GetAdaptiveScores();
                var cbStats = stats.CircuitBreakerStats;
                
                Console.WriteLine($"统计: Hit率={stats.HitRatio:P1}, 缓存数={stats.CachedItemsCount}, " +
                    $"自适应刷新={stats.AdaptiveRefreshCount}");
                    
                Console.WriteLine($"熔断器: {cbStats.State}, 成功率={(cbStats.TotalRequests > 0 ? (double)cbStats.SuccessfulRequests / cbStats.TotalRequests : 1.0):P1}");
                
                // 显示前3的自适应得分
                var topScores = scores.OrderByDescending(kvp => kvp.Value).Take(3);
                var scoreDisplay = string.Join(", ", topScores.Select(kvp => $"{kvp.Key}:{kvp.Value:F2}"));
                Console.WriteLine($"Top3得分: {scoreDisplay}");
                
                await Task.Delay(2000);
            }
            
            Console.WriteLine("\n3. 最终报告...");
            var finalStats = cache.GetStatistics();
            var finalScores = cache.GetAdaptiveScores();
            
            Console.WriteLine($"\n性能指标:");
            Console.WriteLine($"  总请求数: {finalStats.TotalRequests}");
            Console.WriteLine($"  命中率: {finalStats.HitRatio:P2}");
            Console.WriteLine($"  平均加载次数: {finalStats.LoadCount}");
            
            Console.WriteLine($"\n刷新统计:");
            Console.WriteLine($"  标准刷新: {finalStats.AutoRefreshCount}");
            Console.WriteLine($"  自适应刷新: {finalStats.AdaptiveRefreshCount}");
            Console.WriteLine($"  强制刷新: {finalStats.ForceRefreshCount}");
            
            Console.WriteLine($"\n容错统计:");
            Console.WriteLine($"  降级数据命中: {finalStats.StaleDataHits}");
            Console.WriteLine($"  熔断器降级: {finalStats.CircuitBreakerFallbacks}");
            Console.WriteLine($"  过期数据存储: {finalStats.StaleDataCount}");
            
            Console.WriteLine($"\n自适应得分排行 (Top 8):");
            foreach (var kvp in finalScores.OrderByDescending(x => x.Value).Take(8))
            {
                var level = kvp.Value >= 0.3 ? "高" : kvp.Value >= 0.1 ? "中" : "低";
                Console.WriteLine($"  {kvp.Key}: {kvp.Value:F3} ({level}优先级)");
            }
            
            Console.WriteLine($"\n熔断器最终状态:");
            var finalCbStats = finalStats.CircuitBreakerStats;
            Console.WriteLine($"  状态: {finalCbStats.State}");
            Console.WriteLine($"  总请求: {finalCbStats.TotalRequests}");
            Console.WriteLine($"  成功率: {(finalCbStats.TotalRequests > 0 ? (double)finalCbStats.SuccessfulRequests / finalCbStats.TotalRequests : 1.0):P2}");
            Console.WriteLine($"  最近失败率: {finalCbStats.RecentFailureRate:P2}");
        }
        finally
        {
            cache.Dispose();
            (logger as IDisposable)?.Dispose();
            Console.WriteLine("\n=== 演示结束 ===");
        }
    }
}