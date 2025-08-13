using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Serilog;

// 数据变化比较器接口 - 编译时类型安全
public interface IDataChangeDetector<TValue>
{
    bool HasChanged(TValue oldValue, TValue newValue);
    double CalculateChangeRate(TValue oldValue, TValue newValue);
}

// 默认字符串比较器实现
public class StringChangeDetector : IDataChangeDetector<string>
{
    public bool HasChanged(string oldValue, string newValue)
    {
        return !string.Equals(oldValue, newValue, StringComparison.Ordinal);
    }

    public double CalculateChangeRate(string oldValue, string newValue)
    {
        if (oldValue == null && newValue == null) return 0.0;
        if (oldValue == null || newValue == null) return 1.0;
        
        // 使用Levenshtein距离计算相似度
        var maxLen = Math.Max(oldValue.Length, newValue.Length);
        if (maxLen == 0) return 0.0;
        
        var distance = LevenshteinDistance(oldValue, newValue);
        return (double)distance / maxLen;
    }

    private int LevenshteinDistance(string s1, string s2)
    {
        var matrix = new int[s1.Length + 1, s2.Length + 1];
        
        for (int i = 0; i <= s1.Length; i++) matrix[i, 0] = i;
        for (int j = 0; j <= s2.Length; j++) matrix[0, j] = j;
        
        for (int i = 1; i <= s1.Length; i++)
        {
            for (int j = 1; j <= s2.Length; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(Math.Min(
                    matrix[i - 1, j] + 1,
                    matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }
        
        return matrix[s1.Length, s2.Length];
    }
}

// 通用对象比较器（基于HashCode，适用于值类型和实现了合适Equals的类型）
public class GenericChangeDetector<TValue> : IDataChangeDetector<TValue>
{
    public bool HasChanged(TValue oldValue, TValue newValue)
    {
        return !EqualityComparer<TValue>.Default.Equals(oldValue, newValue);
    }

    public double CalculateChangeRate(TValue oldValue, TValue newValue)
    {
        return HasChanged(oldValue, newValue) ? 1.0 : 0.0;
    }
}

// 刷新任务类型
public enum RefreshTaskType
{
    Standard,      // 标准自动刷新
    Force,         // 强制刷新
    Adaptive,      // 自适应刷新
    Manual         // 手动刷新
}

// 刷新任务
public class RefreshTask<TKey>
{
    public TKey Key { get; set; }
    public RefreshTaskType TaskType { get; set; }
    public DateTime ScheduledAt { get; set; }
    public int Priority { get; set; } // 数值越小优先级越高
}

// 统一刷新调度器
public class RefreshScheduler<TKey, TValue> : IDisposable
{
    private readonly IDataLoader<TKey, TValue> _dataLoader;
    private readonly IDataChangeDetector<TValue> _changeDetector;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly CacheConfig _config;
    private readonly ILogger _logger;
    private readonly Func<TKey, CacheItem<TValue>> _getCacheItem;
    private readonly Action<TKey, TValue, bool> _updateCache;
    
    // 优先级队列用于任务调度（使用SortedDictionary模拟）
    private readonly SortedDictionary<long, Queue<RefreshTask<TKey>>> _taskQueue = new();
    private readonly object _queueLock = new object();
    
    // 正在执行的任务追踪
    private readonly ConcurrentDictionary<TKey, CancellationTokenSource> _runningTasks = new();
    
    // 定时器
    private readonly Timer _processingTimer;
    private volatile bool _disposed = false;
    
    // 统计
    private long _totalRefreshTasks;
    private long _successfulRefreshTasks;
    private long _failedRefreshTasks;
    private long _adaptiveIntervalAdjustments;

    public long TotalRefreshTasks => Interlocked.Read(ref _totalRefreshTasks);
    public long SuccessfulRefreshTasks => Interlocked.Read(ref _successfulRefreshTasks);
    public long FailedRefreshTasks => Interlocked.Read(ref _failedRefreshTasks);
    public long AdaptiveIntervalAdjustments => Interlocked.Read(ref _adaptiveIntervalAdjustments);

    public RefreshScheduler(
        IDataLoader<TKey, TValue> dataLoader,
        IDataChangeDetector<TValue> changeDetector,
        CircuitBreaker circuitBreaker,
        CacheConfig config,
        ILogger logger,
        Func<TKey, CacheItem<TValue>> getCacheItem,
        Action<TKey, TValue, bool> updateCache)
    {
        _dataLoader = dataLoader ?? throw new ArgumentNullException(nameof(dataLoader));
        _changeDetector = changeDetector ?? throw new ArgumentNullException(nameof(changeDetector));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
        _getCacheItem = getCacheItem ?? throw new ArgumentNullException(nameof(getCacheItem));
        _updateCache = updateCache ?? throw new ArgumentNullException(nameof(updateCache));
        
        // 每500ms处理一次任务队列
        _processingTimer = new Timer(ProcessTaskQueue, null, TimeSpan.FromMilliseconds(500), 
                                   TimeSpan.FromMilliseconds(500));
    }

    // 调度刷新任务
    public void ScheduleRefresh(TKey key, RefreshTaskType taskType, DateTime? scheduledTime = null)
    {
        if (_disposed) return;
        
        var task = new RefreshTask<TKey>
        {
            Key = key,
            TaskType = taskType,
            ScheduledAt = scheduledTime ?? DateTime.UtcNow,
            Priority = GetPriority(taskType)
        };
        
        lock (_queueLock)
        {
            var timestamp = task.ScheduledAt.Ticks + task.Priority; // 结合时间和优先级
            
            if (!_taskQueue.ContainsKey(timestamp))
                _taskQueue[timestamp] = new Queue<RefreshTask<TKey>>();
                
            _taskQueue[timestamp].Enqueue(task);
        }
        
        Interlocked.Increment(ref _totalRefreshTasks);
        _logger?.Debug("Scheduled {TaskType} refresh for key '{Key}' at {Time}", 
                      taskType, key, task.ScheduledAt);
    }

    // 批量调度
    public void ScheduleBatchRefresh(IEnumerable<TKey> keys, RefreshTaskType taskType, DateTime? scheduledTime = null)
    {
        foreach (var key in keys)
        {
            ScheduleRefresh(key, taskType, scheduledTime);
        }
    }

    // 取消特定键的刷新任务
    public void CancelRefresh(TKey key)
    {
        if (_runningTasks.TryGetValue(key, out var cts))
        {
            cts.Cancel();
        }
    }

    private int GetPriority(RefreshTaskType taskType)
    {
        return taskType switch
        {
            RefreshTaskType.Manual => 0,    // 最高优先级
            RefreshTaskType.Force => 1,     // 强制刷新
            RefreshTaskType.Adaptive => 2,  // 自适应刷新
            RefreshTaskType.Standard => 3,  // 标准刷新
            _ => 4
        };
    }

    private void ProcessTaskQueue(object state)
    {
        if (_disposed || _circuitBreaker.State == CircuitBreakerState.Open) return;

        var tasksToProcess = new List<RefreshTask<TKey>>();
        var now = DateTime.UtcNow;

        // 收集到期的任务
        lock (_queueLock)
        {
            var keysToRemove = new List<long>();
            
            foreach (var kvp in _taskQueue)
            {
                if (kvp.Value.Count == 0)
                {
                    keysToRemove.Add(kvp.Key);
                    continue;
                }

                var task = kvp.Value.Peek();
                if (task.ScheduledAt <= now)
                {
                    tasksToProcess.Add(kvp.Value.Dequeue());
                    if (kvp.Value.Count == 0)
                        keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
                _taskQueue.Remove(key);
        }

        // 处理任务（按优先级分组批处理）
        var taskGroups = tasksToProcess
            .GroupBy(t => t.TaskType)
            .OrderBy(g => GetPriority(g.Key));

        foreach (var group in taskGroups)
        {
            _ = ProcessTaskGroup(group.ToList());
        }
    }

    private async Task ProcessTaskGroup(List<RefreshTask<TKey>> tasks)
    {
        if (!tasks.Any()) return;

        var taskType = tasks.First().TaskType;
        var batchSize = Math.Min(tasks.Count, _config.MaxAutoRefreshBatchSize);
        
        // 分批处理
        for (int i = 0; i < tasks.Count; i += batchSize)
        {
            var batch = tasks.Skip(i).Take(batchSize).ToList();
            await ProcessBatch(batch, taskType);
        }
    }

    private async Task ProcessBatch(List<RefreshTask<TKey>> batch, RefreshTaskType taskType)
    {
        var keys = batch.Select(t => t.Key).ToList();
        var cancellationSources = new Dictionary<TKey, CancellationTokenSource>();

        try
        {
            // 为每个键创建取消令牌
            foreach (var key in keys)
            {
                var cts = new CancellationTokenSource();
                cancellationSources[key] = cts;
                _runningTasks[key] = cts;
            }

            // 执行批量加载
            var newValues = await _circuitBreaker.ExecuteAsync(async () =>
            {
                return await _dataLoader.LoadBatchAsync(keys);
            });

            // 处理结果
            foreach (var kvp in newValues)
            {
                if (cancellationSources[kvp.Key].Token.IsCancellationRequested)
                    continue;

                await ProcessSingleRefreshResult(kvp.Key, kvp.Value, taskType);
                Interlocked.Increment(ref _successfulRefreshTasks);
            }

            _logger?.Debug("Successfully processed {Count} {TaskType} refresh tasks", 
                          newValues.Count, taskType);
        }
        catch (CircuitBreakerOpenException)
        {
            _logger?.Warning("Batch refresh cancelled due to circuit breaker open");
            Interlocked.Add(ref _failedRefreshTasks, keys.Count);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to process batch refresh for {Count} keys", keys.Count);
            Interlocked.Add(ref _failedRefreshTasks, keys.Count);
        }
        finally
        {
            // 清理运行中的任务追踪
            foreach (var key in keys)
            {
                _runningTasks.TryRemove(key, out _);
                if (cancellationSources.TryGetValue(key, out var cts))
                    cts.Dispose();
            }
        }
    }

    private async Task ProcessSingleRefreshResult(TKey key, TValue newValue, RefreshTaskType taskType)
    {
        var cacheItem = _getCacheItem(key);
        if (cacheItem == null) return;

        var oldValue = cacheItem.Value;
        var hasChanged = _changeDetector.HasChanged(oldValue, newValue);
        
        // 更新缓存
        _updateCache(key, newValue, true);
        
        // 如果是自适应刷新，根据数据变化情况调整间隔
        if (taskType == RefreshTaskType.Adaptive)
        {
            await UpdateAdaptiveInterval(key, cacheItem, oldValue, newValue, hasChanged);
        }

        _logger?.Debug("Processed {TaskType} refresh for key '{Key}', data changed: {Changed}", 
                      taskType, key, hasChanged);
    }

    private async Task UpdateAdaptiveInterval(TKey key, CacheItem<TValue> cacheItem, 
                                            TValue oldValue, TValue newValue, bool hasChanged)
    {
        var now = DateTime.UtcNow;
        var timeSinceLastCheck = now - cacheItem.LastDataChangeCheckTime;
        
        if (hasChanged)
        {
            // 数据发生变化，缩短检查间隔
            var changeRate = _changeDetector.CalculateChangeRate(oldValue, newValue);
            var adjustmentFactor = Math.Max(0.5, 1.0 - changeRate); // 变化越大，间隔缩短得越多
            
            cacheItem.EstimatedDataChangeInterval = TimeSpan.FromTicks(
                Math.Max(TimeSpan.FromMinutes(1).Ticks, 
                        (long)(cacheItem.EstimatedDataChangeInterval.Ticks * adjustmentFactor)));
                        
            _logger?.Debug("Data changed for key '{Key}', shortened interval to {Interval}", 
                          key, cacheItem.EstimatedDataChangeInterval);
        }
        else
        {
            // 数据未变化，逐渐延长检查间隔
            var growthFactor = Math.Min(2.0, 1.0 + (timeSinceLastCheck.TotalMinutes / 60.0)); // 最多延长2倍
            
            cacheItem.EstimatedDataChangeInterval = TimeSpan.FromTicks(
                Math.Min(TimeSpan.FromHours(4).Ticks, // 最大4小时
                        (long)(cacheItem.EstimatedDataChangeInterval.Ticks * growthFactor)));
                        
            _logger?.Debug("Data unchanged for key '{Key}', extended interval to {Interval}", 
                          key, cacheItem.EstimatedDataChangeInterval);
        }
        
        cacheItem.LastDataChangeCheckTime = now;
        Interlocked.Increment(ref _adaptiveIntervalAdjustments);
    }

    // 获取调度器统计信息
    public RefreshSchedulerStatistics GetStatistics()
    {
        lock (_queueLock)
        {
            var pendingTasks = _taskQueue.Values.Sum(q => q.Count);
            
            return new RefreshSchedulerStatistics
            {
                TotalRefreshTasks = this.TotalRefreshTasks,
                SuccessfulRefreshTasks = this.SuccessfulRefreshTasks,
                FailedRefreshTasks = this.FailedRefreshTasks,
                AdaptiveIntervalAdjustments = this.AdaptiveIntervalAdjustments,
                PendingTasks = pendingTasks,
                RunningTasks = _runningTasks.Count,
                SuccessRate = this.TotalRefreshTasks > 0 ? 
                    (double)this.SuccessfulRefreshTasks / this.TotalRefreshTasks : 1.0
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _processingTimer?.Dispose();
        
        // 取消所有运行中的任务
        foreach (var cts in _runningTasks.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _runningTasks.Clear();
        
        lock (_queueLock)
        {
            _taskQueue.Clear();
        }
    }
}

// 调度器统计信息
public class RefreshSchedulerStatistics
{
    public long TotalRefreshTasks { get; set; }
    public long SuccessfulRefreshTasks { get; set; }
    public long FailedRefreshTasks { get; set; }
    public long AdaptiveIntervalAdjustments { get; set; }
    public int PendingTasks { get; set; }
    public int RunningTasks { get; set; }
    public double SuccessRate { get; set; }
}

// 改进的CacheItem，移除复杂的自适应逻辑
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
}

// 改进的缓存配置，移除一些不必要的选项
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
    public double RefreshThreshold { get; set; } = 0.8;
    public int MaxAutoRefreshBatchSize { get; set; } = 50;
    public AutoRefreshStrategy AutoRefreshStrategy { get; set; } = AutoRefreshStrategy.LeastRecentlyUsed;
    
    // 强制刷新配置
    public bool EnableForceRefresh { get; set; } = false;
    public TimeSpan ForceRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);
    
    // 自适应刷新配置
    public bool EnableAdaptiveRefresh { get; set; } = false;
    
    // 熔断器配置
    public CircuitBreakerConfig CircuitBreaker { get; set; } = new CircuitBreakerConfig();
    
    // 降级策略配置
    public bool EnableStaleDataFallback { get; set; } = true;
    public TimeSpan MaxStaleDataAge { get; set; } = TimeSpan.FromHours(1);
}

// 工厂接口，用于创建数据变化检测器
public interface IDataChangeDetectorFactory
{
    IDataChangeDetector<TValue> CreateDetector<TValue>();
}

// 默认工厂实现
public class DefaultDataChangeDetectorFactory : IDataChangeDetectorFactory
{
    public IDataChangeDetector<TValue> CreateDetector<TValue>()
    {
        // 特殊处理常见类型
        if (typeof(TValue) == typeof(string))
        {
            return (IDataChangeDetector<TValue>)(object)new StringChangeDetector();
        }
        
        // 默认使用通用比较器
        return new GenericChangeDetector<TValue>();
    }
}

// 在主缓存类中的集成使用示例
public partial class ImprovedAdvancedMemoryCache<TKey, TValue> : IDisposable
{
    private IMemoryCache _memoryCache;
    private readonly IDataLoader<TKey, TValue> _dataLoader;
    private readonly CacheConfig _config;
    private readonly Func<TKey, string> _keySerializer;
    private readonly ILogger _logger;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RefreshScheduler<TKey, TValue> _refreshScheduler;
    private readonly IDataChangeDetector<TValue> _changeDetector;

    // 其他字段...
    private readonly ConcurrentDictionary<TKey, Task<TValue>> _loadingTasks = new();
    private readonly ConcurrentDictionary<TKey, CacheItem<TValue>> _staleDataStorage = new();
    private readonly ConcurrentDictionary<TKey, byte> _cacheKeys = new();
    private volatile bool _disposed = false;

    public ImprovedAdvancedMemoryCache(
        IDataLoader<TKey, TValue> dataLoader,
        IOptions<CacheConfig> config,
        ILogger logger = null,
        Func<TKey, string> keySerializer = null,
        IDataChangeDetectorFactory changeDetectorFactory = null)
    {
        _dataLoader = dataLoader ?? throw new ArgumentNullException(nameof(dataLoader));
        _config = config?.Value ?? new CacheConfig();
        _logger = logger?.ForContext<ImprovedAdvancedMemoryCache<TKey, TValue>>();
        _keySerializer = keySerializer ?? (key => key.ToString());
        
        _circuitBreaker = new CircuitBreaker(_config.CircuitBreaker, _logger);
        
        // 创建数据变化检测器
        var detectorFactory = changeDetectorFactory ?? new DefaultDataChangeDetectorFactory();
        _changeDetector = detectorFactory.CreateDetector<TValue>();
        
        // 创建统一刷新调度器
        _refreshScheduler = new RefreshScheduler<TKey, TValue>(
            _dataLoader,
            _changeDetector,
            _circuitBreaker,
            _config,
            _logger,
            GetCacheItemForScheduler,
            SetCacheForScheduler
        );

        _memoryCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _config.MaxCacheSize
        });

        // 启动自动刷新（通过调度器）
        if (_config.EnableAutoRefresh)
        {
            StartAutoRefreshScheduling();
        }
    }

    private void StartAutoRefreshScheduling()
    {
        _ = Task.Run(async () =>
        {
            while (!_disposed)
            {
                try
                {
                    await ScheduleAutoRefreshTasks();
                    
                    var interval = _config.EnableForceRefresh 
                        ? _config.ForceRefreshInterval 
                        : _config.AutoRefreshInterval;
                    await Task.Delay(interval);
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Error in auto refresh scheduling");
                    await Task.Delay(TimeSpan.FromSeconds(5)); // 出错后短暂延迟
                }
            }
        });
    }

    private async Task ScheduleAutoRefreshTasks()
    {
        if (_circuitBreaker.State == CircuitBreakerState.Open) return;

        var keysToRefresh = GetKeysForAutoRefresh();
        if (!keysToRefresh.Any()) return;

        var taskType = _config.EnableForceRefresh 
            ? RefreshTaskType.Force 
            : _config.EnableAdaptiveRefresh 
                ? RefreshTaskType.Adaptive 
                : RefreshTaskType.Standard;

        _refreshScheduler.ScheduleBatchRefresh(keysToRefresh, taskType);
        
        _logger?.Debug("Scheduled {Count} {TaskType} refresh tasks", keysToRefresh.Count, taskType);
    }

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
                        bool shouldRefresh = false;
                        
                        if (_config.EnableForceRefresh)
                        {
                            shouldRefresh = item.ShouldForceRefresh(_config.ForceRefreshInterval);
                        }
                        else if (_config.EnableAdaptiveRefresh)
                        {
                            shouldRefresh = item.ShouldAdaptiveRefresh();
                        }
                        else
                        {
                            shouldRefresh = item.ShouldRefresh(_config.RefreshThreshold);
                        }
                        
                        if (shouldRefresh)
                        {
                            cacheItems[key] = item;
                        }
                    }
                }
                else
                {
                    _cacheKeys.TryRemove(key, out _);
                }
            }
            catch (ObjectDisposedException)
            {
                return new List<TKey>();
            }
        }

        // 应用刷新策略选择键
        return ApplyRefreshStrategy(cacheItems).Take(_config.MaxAutoRefreshBatchSize).ToList();
    }

    private IEnumerable<TKey> ApplyRefreshStrategy(Dictionary<TKey, CacheItem<TValue>> candidates)
    {
        return _config.AutoRefreshStrategy switch
        {
            AutoRefreshStrategy.All => candidates.Keys,
            AutoRefreshStrategy.LeastRecentlyUsed => candidates
                .OrderBy(kvp => kvp.Value.LastAccessedAt)
                .Select(kvp => kvp.Key),
            AutoRefreshStrategy.MostFrequentlyUsed => candidates
                .OrderByDescending(kvp => Interlocked.Read(ref kvp.Value.AccessCount))
                .Select(kvp => kvp.Key),
            AutoRefreshStrategy.OldestFirst => candidates
                .OrderBy(kvp => kvp.Value.CreatedAt)
                .Select(kvp => kvp.Key),
            AutoRefreshStrategy.ExpirationBased => candidates
                .OrderBy(kvp => kvp.Value.ExpiresAt)
                .Select(kvp => kvp.Key),
            AutoRefreshStrategy.AdaptivePriority => candidates
                .Where(kvp => kvp.Value.ShouldAdaptiveRefresh())
                .OrderBy(kvp => kvp.Value.EstimatedDataChangeInterval)
                .Select(kvp => kvp.Key),
            _ => candidates.Keys
        };
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

    // 手动触发刷新
    public void TriggerRefresh(TKey key, RefreshTaskType taskType = RefreshTaskType.Manual)
    {
        _refreshScheduler.ScheduleRefresh(key, taskType);
    }

    public void TriggerBatchRefresh(IEnumerable<TKey> keys, RefreshTaskType taskType = RefreshTaskType.Manual)
    {
        _refreshScheduler.ScheduleBatchRefresh(keys, taskType);
    }

    // 获取综合统计信息
    public ComprehensiveCacheStatistics GetComprehensiveStatistics()
    {
        var refreshStats = _refreshScheduler.GetStatistics();
        var cbStats = _circuitBreaker.GetStatistics();

        return new ComprehensiveCacheStatistics
        {
            // 基础缓存统计
            CachedItemsCount = _cacheKeys.Count,
            StaleDataCount = _staleDataStorage.Count,
            
            // 刷新调度器统计
            RefreshSchedulerStats = refreshStats,
            
            // 熔断器统计
            CircuitBreakerStats = cbStats,
            
            // 数据变化统计
            DataChangeDetectorType = _changeDetector.GetType().Name
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
    public string DataChangeDetectorType { get; set; }
}

// 熔断器相关类型（保持原有实现）
public enum CircuitBreakerState
{
    Closed, Open, HalfOpen
}

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

// 简化的熔断器实现（保持核心功能）
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
public class ImprovedCacheUsageExample
{
    public static async Task Example()
    {
        Console.WriteLine("=== 改进的自适应缓存系统演示 ===");

        var config = new CacheConfig
        {
            MaxCacheSize = 100,
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EnableAutoRefresh = true,
            EnableAdaptiveRefresh = true,
            AutoRefreshInterval = TimeSpan.FromSeconds(15),
            MaxAutoRefreshBatchSize = 20,
            AutoRefreshStrategy = AutoRefreshStrategy.AdaptivePriority,
            
            CircuitBreaker = new CircuitBreakerConfig
            {
                Enabled = true,
                FailureThreshold = 3,
                OpenTimeout = TimeSpan.FromSeconds(20)
            },
            
            EnableStaleDataFallback = true,
            MaxStaleDataAge = TimeSpan.FromMinutes(30)
        };

        var dataLoader = new SimpleDataLoader();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();

        // 使用自定义的数据变化检测器工厂
        var changeDetectorFactory = new CustomDataChangeDetectorFactory();

        var cache = new ImprovedAdvancedMemoryCache<string, string>(
            dataLoader, Options.Create(config), logger, null, changeDetectorFactory);

        try
        {
            Console.WriteLine("\n1. 预加载数据并建立访问模式...");
            var keys = new[] { "user:1", "user:2", "user:3", "config:app", "config:db" };
            
            // 初始加载
            foreach (var key in keys)
            {
                try
                {
                    var value = await cache.GetAsync(key);
                    Console.WriteLine($"Loaded {key}: {value}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load {key}: {ex.Message}");
                }
            }

            Console.WriteLine("\n2. 模拟不同的访问模式...");
            
            // 运行30秒，观察自适应刷新的行为
            var endTime = DateTime.UtcNow.AddSeconds(30);
            var round = 0;

            while (DateTime.UtcNow < endTime)
            {
                round++;
                Console.WriteLine($"\n--- 第 {round} 轮 ---");

                // 高频访问 user:1 (应该得到较短的刷新间隔)
                for (int i = 0; i < 5; i++)
                {
                    try { await cache.GetAsync("user:1"); } 
                    catch { }
                    await Task.Delay(200);
                }

                // 中频访问 config:app (应该得到中等刷新间隔)
                if (round % 2 == 0)
                {
                    try { await cache.GetAsync("config:app"); } 
                    catch { }
                }

                // 低频访问其他键 (应该得到较长的刷新间隔)
                if (round % 4 == 0)
                {
                    try 
                    { 
                        await cache.GetAsync("user:2");
                        await cache.GetAsync("config:db");
                    } 
                    catch { }
                }

                // 手动触发一些自适应刷新任务
                if (round % 3 == 0)
                {
                    cache.TriggerRefresh("user:1", RefreshTaskType.Adaptive);
                }

                // 显示统计信息
                var stats = cache.GetComprehensiveStatistics();
                var refreshStats = stats.RefreshSchedulerStats;
                var cbStats = stats.CircuitBreakerStats;

                Console.WriteLine($"缓存统计: Items={stats.CachedItemsCount}, Stale={stats.StaleDataCount}");
                Console.WriteLine($"刷新统计: Total={refreshStats.TotalRefreshTasks}, " +
                                $"Success={refreshStats.SuccessfulRefreshTasks}, " +
                                $"Adaptive Adjustments={refreshStats.AdaptiveIntervalAdjustments}");
                Console.WriteLine($"调度器: Pending={refreshStats.PendingTasks}, " +
                                $"Running={refreshStats.RunningTasks}, " +
                                $"Success Rate={refreshStats.SuccessRate:P1}");
                Console.WriteLine($"熔断器: {cbStats.State}, " +
                                $"Success Rate={(cbStats.TotalRequests > 0 ? (double)cbStats.SuccessfulRequests / cbStats.TotalRequests : 1.0):P1}");

                await Task.Delay(3000);
            }

            Console.WriteLine("\n3. 最终报告...");
            var finalStats = cache.GetComprehensiveStatistics();
            
            Console.WriteLine($"\n最终统计:");
            Console.WriteLine($"数据变化检测器: {finalStats.DataChangeDetectorType}");
            Console.WriteLine($"刷新任务统计:");
            Console.WriteLine($"  总任务数: {finalStats.RefreshSchedulerStats.TotalRefreshTasks}");
            Console.WriteLine($"  成功率: {finalStats.RefreshSchedulerStats.SuccessRate:P2}");
            Console.WriteLine($"  自适应间隔调整: {finalStats.RefreshSchedulerStats.AdaptiveIntervalAdjustments}");
            
            Console.WriteLine($"\n期望结果:");
            Console.WriteLine($"- user:1 应该有最多的自适应间隔调整 (高频访问)");
            Console.WriteLine($"- config:app 应该有适中的调整 (中频访问)");
            Console.WriteLine($"- 其他键应该调整较少 (低频访问)");
            Console.WriteLine($"- 数据变化检测应该影响刷新间隔的动态调整");
        }
        finally
        {
            cache.Dispose();
            (logger as IDisposable)?.Dispose();
            Console.WriteLine("\n缓存已释放");
        }
    }
}

// 自定义数据变化检测器工厂示例
public class CustomDataChangeDetectorFactory : IDataChangeDetectorFactory
{
    public IDataChangeDetector<TValue> CreateDetector<TValue>()
    {
        if (typeof(TValue) == typeof(string))
        {
            return (IDataChangeDetector<TValue>)(object)new EnhancedStringChangeDetector();
        }
        
        return new GenericChangeDetector<TValue>();
    }
}

// 增强的字符串变化检测器
public class EnhancedStringChangeDetector : IDataChangeDetector<string>
{
    public bool HasChanged(string oldValue, string newValue)
    {
        return !string.Equals(oldValue, newValue, StringComparison.Ordinal);
    }

    public double CalculateChangeRate(string oldValue, string newValue)
    {
        if (oldValue == null && newValue == null) return 0.0;
        if (oldValue == null || newValue == null) return 1.0;

        // 对于特定格式的字符串，使用更精确的变化率计算
        if (oldValue.StartsWith("Value for") && newValue.StartsWith("Value for"))
        {
            // 提取时间戳比较
            var oldTime = ExtractTimestamp(oldValue);
            var newTime = ExtractTimestamp(newValue);
            
            if (oldTime.HasValue && newTime.HasValue)
            {
                var timeDiff = Math.Abs((newTime.Value - oldTime.Value).TotalSeconds);
                return Math.Min(1.0, timeDiff / 3600.0); // 标准化到1小时内的变化
            }
        }

        // 回退到标准字符串比较
        return HasChanged(oldValue, newValue) ? 1.0 : 0.0;
    }

    private DateTime? ExtractTimestamp(string value)
    {
        try
        {
            var parts = value.Split(new[] { " loaded at " }, StringSplitOptions.None);
            if (parts.Length == 2 && DateTime.TryParse(parts[1], out var timestamp))
            {
                return timestamp;
            }
        }
        catch { }
        
        return null;
    }
}

// 简单的数据加载器实现（用于演示）
public class SimpleDataLoader : IDataLoader<string, string>
{
    private static int _callCount = 0;
    private readonly Random _random = new Random();

    public async Task<string> LoadAsync(string key)
    {
        Interlocked.Increment(ref _callCount);
        
        // 模拟间歇性故障
        if (_callCount % 8 == 0)
        {
            throw new InvalidOperationException($"Simulated failure for key: {key}");
        }
        
        await Task.Delay(_random.Next(50, 200));
        return $"Value for {key} loaded at {DateTime.Now}";
    }

    public async Task<Dictionary<string, string>> LoadBatchAsync(IEnumerable<string> keys)
    {
        var keyList = keys.ToList();
        Interlocked.Add(ref _callCount, keyList.Count);
        
        if (_callCount % 10 == 0)
        {
            throw new TimeoutException("Simulated batch timeout");
        }
        
        await Task.Delay(_random.Next(100, 400));
        
        var result = new Dictionary<string, string>();
        foreach (var key in keyList)
        {
            result[key] = $"Batch value for {key} loaded at {DateTime.Now}";
        }
        return result;
    }
}