using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

// ===== 1. DI 注册扩展 =====
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册缓存树构建相关服务
    /// </summary>
    public static IServiceCollection AddCacheTreeServices<TKey, TValue>(
        this IServiceCollection services,
        Func<IServiceProvider, IEnumerable<TKey>> getAllKeys,
        Func<IServiceProvider, TKey, CacheItem<TValue>> getCacheItem,
        Func<TKey, string> keySerializer = null)
    {
        // 注册树构建器
        services.AddTransient<ImprovedCacheTreeBuilder<TKey, TValue>>(provider =>
        {
            var logger = provider.GetService<ILogger<ImprovedCacheTreeBuilder<TKey, TValue>>>();
            return new ImprovedCacheTreeBuilder<TKey, TValue>(
                () => getAllKeys(provider),
                key => getCacheItem(provider, key),
                keySerializer,
                logger
            );
        });

        // 注册树服务
        services.AddScoped<ICacheTreeService<TKey, TValue>, CacheTreeService<TKey, TValue>>();

        return services;
    }
}

// ===== 2. 服务接口和实现 =====
public interface ICacheTreeService<TKey, TValue>
{
    ElementNode BuildTree();
    ElementNode QueryElement(string path);
    AttributeNode<T> QueryAttribute<T>(string elementPath, string attributeName);
    Dictionary<string, TreeNode> GetAllAttributes(string elementPath = null);
    ElementStatistics GetStatistics(string elementPath = null);
}

public class CacheTreeService<TKey, TValue> : ICacheTreeService<TKey, TValue>
{
    private readonly ImprovedCacheTreeBuilder<TKey, TValue> _treeBuilder;
    private readonly ILogger<CacheTreeService<TKey, TValue>> _logger;
    private ElementNode _cachedTree;

    public CacheTreeService(
        ImprovedCacheTreeBuilder<TKey, TValue> treeBuilder,
        ILogger<CacheTreeService<TKey, TValue>> logger)
    {
        _treeBuilder = treeBuilder ?? throw new ArgumentNullException(nameof(treeBuilder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ElementNode BuildTree()
    {
        try
        {
            _cachedTree = _treeBuilder.BuildImprovedTree();
            _logger.LogInformation("Cache tree built successfully");
            return _cachedTree;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build cache tree");
            throw;
        }
    }

    public ElementNode QueryElement(string path)
    {
        EnsureTreeBuilt();
        
        if (string.IsNullOrWhiteSpace(path))
            return _cachedTree;
            
        return _cachedTree.FindElement(path);
    }

    public AttributeNode<T> QueryAttribute<T>(string elementPath, string attributeName)
    {
        EnsureTreeBuilt();
        return _cachedTree.FindAttribute<T>(elementPath, attributeName);
    }

    public Dictionary<string, TreeNode> GetAllAttributes(string elementPath = null)
    {
        EnsureTreeBuilt();
        
        if (string.IsNullOrWhiteSpace(elementPath))
            return _cachedTree.GetAllAttributes();
            
        var element = _cachedTree.FindElement(elementPath);
        return element?.GetAllAttributes() ?? new Dictionary<string, TreeNode>();
    }

    public ElementStatistics GetStatistics(string elementPath = null)
    {
        EnsureTreeBuilt();
        
        if (string.IsNullOrWhiteSpace(elementPath))
            return _cachedTree.GetStatistics();
            
        var element = _cachedTree.FindElement(elementPath);
        return element?.GetStatistics();
    }

    private void EnsureTreeBuilt()
    {
        if (_cachedTree == null)
        {
            BuildTree();
        }
    }
}

// ===== 3. API 模型 =====
public class CacheTreeQueryRequest
{
    /// <summary>
    /// 查询类型: "element", "attribute", "statistics", "all_attributes"
    /// </summary>
    [Required]
    public string QueryType { get; set; }

    /// <summary>
    /// 元素路径，如 "Unit1/Equipment1"
    /// </summary>
    public string ElementPath { get; set; }

    /// <summary>
    /// 属性名称（当QueryType为"attribute"时必需）
    /// </summary>
    public string AttributeName { get; set; }

    /// <summary>
    /// 是否强制重新构建树
    /// </summary>
    public bool ForceRebuild { get; set; } = false;
}

public class CacheTreeQueryResponse
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public object Data { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// ===== 4. 控制器实现 =====
[ApiController]
[Route("api/[controller]")]
public class CacheTreeController : ControllerBase
{
    private readonly ICacheTreeService<string, object> _cacheTreeService;
    private readonly ILogger<CacheTreeController> _logger;

    public CacheTreeController(
        ICacheTreeService<string, object> cacheTreeService,
        ILogger<CacheTreeController> logger)
    {
        _cacheTreeService = cacheTreeService ?? throw new ArgumentNullException(nameof(cacheTreeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 查询缓存树数据
    /// </summary>
    [HttpPost("query")]
    public IActionResult Query([FromBody] CacheTreeQueryRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new CacheTreeQueryResponse
            {
                Success = false,
                Message = "Invalid request model",
                Data = ModelState
            });
        }

        try
        {
            _logger.LogInformation("Processing cache tree query: {QueryType}, Path: {ElementPath}, Attribute: {AttributeName}", 
                request.QueryType, request.ElementPath, request.AttributeName);

            if (request.ForceRebuild)
            {
                _cacheTreeService.BuildTree();
            }

            var response = request.QueryType?.ToLowerInvariant() switch
            {
                "element" => QueryElement(request),
                "attribute" => QueryAttribute(request),
                "statistics" => QueryStatistics(request),
                "all_attributes" => QueryAllAttributes(request),
                "tree" => QueryFullTree(),
                _ => new CacheTreeQueryResponse
                {
                    Success = false,
                    Message = $"Unsupported query type: {request.QueryType}"
                }
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing cache tree query");
            return StatusCode(500, new CacheTreeQueryResponse
            {
                Success = false,
                Message = "Internal server error occurred"
            });
        }
    }

    /// <summary>
    /// 重新构建缓存树
    /// </summary>
    [HttpPost("rebuild")]
    public IActionResult RebuildTree()
    {
        try
        {
            _logger.LogInformation("Rebuilding cache tree");
            var tree = _cacheTreeService.BuildTree();
            
            return Ok(new CacheTreeQueryResponse
            {
                Success = true,
                Message = "Cache tree rebuilt successfully",
                Data = new
                {
                    Statistics = tree.GetStatistics(),
                    ElementCount = tree.ChildElements.Count,
                    AttributeCount = tree.Attributes.Count
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rebuilding cache tree");
            return StatusCode(500, new CacheTreeQueryResponse
            {
                Success = false,
                Message = "Failed to rebuild cache tree"
            });
        }
    }

    private CacheTreeQueryResponse QueryElement(CacheTreeQueryRequest request)
    {
        var element = _cacheTreeService.QueryElement(request.ElementPath);
        
        if (element == null)
        {
            return new CacheTreeQueryResponse
            {
                Success = false,
                Message = $"Element not found at path: {request.ElementPath}"
            };
        }

        return new CacheTreeQueryResponse
        {
            Success = true,
            Message = "Element found",
            Data = element.ToSerializableObject()
        };
    }

    private CacheTreeQueryResponse QueryAttribute(CacheTreeQueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AttributeName))
        {
            return new CacheTreeQueryResponse
            {
                Success = false,
                Message = "AttributeName is required for attribute queries"
            };
        }

        // 因为我们不知道确切的类型，使用基类查询
        var element = _cacheTreeService.QueryElement(request.ElementPath);
        var attribute = element?.GetAttribute(request.AttributeName);

        if (attribute == null)
        {
            return new CacheTreeQueryResponse
            {
                Success = false,
                Message = $"Attribute '{request.AttributeName}' not found at path: {request.ElementPath}"
            };
        }

        return new CacheTreeQueryResponse
        {
            Success = true,
            Message = "Attribute found",
            Data = attribute.ToSerializableObject()
        };
    }

    private CacheTreeQueryResponse QueryStatistics(CacheTreeQueryRequest request)
    {
        var stats = _cacheTreeService.GetStatistics(request.ElementPath);
        
        if (stats == null)
        {
            return new CacheTreeQueryResponse
            {
                Success = false,
                Message = $"Element not found at path: {request.ElementPath}"
            };
        }

        return new CacheTreeQueryResponse
        {
            Success = true,
            Message = "Statistics retrieved",
            Data = stats
        };
    }

    private CacheTreeQueryResponse QueryAllAttributes(CacheTreeQueryRequest request)
    {
        var attributes = _cacheTreeService.GetAllAttributes(request.ElementPath);
        
        // 转换为可序列化的格式
        var serializableAttrs = new Dictionary<string, object>();
        foreach (var attr in attributes)
        {
            serializableAttrs[attr.Key] = attr.Value.ToSerializableObject();
        }

        return new CacheTreeQueryResponse
        {
            Success = true,
            Message = "All attributes retrieved",
            Data = new
            {
                Count = attributes.Count,
                Attributes = serializableAttrs
            }
        };
    }

    private CacheTreeQueryResponse QueryFullTree()
    {
        var tree = _cacheTreeService.QueryElement(null); // 获取根节点
        
        return new CacheTreeQueryResponse
        {
            Success = true,
            Message = "Full tree retrieved",
            Data = tree.ToSerializableObject()
        };
    }
}

// ===== 5. Startup.cs 或 Program.cs 中的配置示例 =====
public class CacheTreeStartupExample
{
    public void ConfigureServices(IServiceCollection services)
    {
        // 添加基础服务
        services.AddControllers();
        services.AddLogging();

        // 示例：注册缓存树服务
        // 这里需要根据你的实际缓存实现来提供函数
        services.AddCacheTreeServices<string, object>(
            // 获取所有缓存键的函数
            provider => GetAllCacheKeys(provider),
            // 根据键获取缓存项的函数
            (provider, key) => GetCacheItem(provider, key),
            // 键序列化函数（可选）
            key => key?.ToString()
        );
    }

    // 示例实现 - 需要根据你的实际缓存替换
    private static IEnumerable<string> GetAllCacheKeys(IServiceProvider provider)
    {
        // 这里应该返回你的实际缓存键
        // 例如从 MemoryCache、Redis 或其他缓存源获取
        return new[] { 
            "cache:Plant1/Unit1|Status", 
            "cache:Plant1/Unit1|Efficiency",
            "cache:Plant1/Unit1/Equipment1|Temperature"
        };
    }

    private static CacheItem<object> GetCacheItem(IServiceProvider provider, string key)
    {
        // 这里应该从你的实际缓存获取数据
        // 示例返回
        return new CacheItem<object>
        {
            Value = "Sample Value",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            LastReadAt = DateTime.UtcNow,
            AccessCount = 10,
            IsStale = false
        };
    }
}