using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

/// <summary>
/// 强类型的树节点定义
/// </summary>
public abstract class TreeNode
{
    public string Name { get; set; }
    public abstract string NodeType { get; }
    public abstract object ToSerializableObject();
}

/// <summary>
/// 改进的元素节点 - 同时支持子元素和属性集合
/// </summary>
public class ElementNode : TreeNode
{
    public override string NodeType => "element";
    
    // 子元素集合（其他ElementNode）
    public Dictionary<string, ElementNode> ChildElements { get; set; } = new Dictionary<string, ElementNode>();
    
    // 属性集合（AttributeNode）
    public Dictionary<string, TreeNode> Attributes { get; set; } = new Dictionary<string, TreeNode>();
    
    // 元素的元数据（可选）
    public ElementMetadata Metadata { get; set; }

    public override object ToSerializableObject()
    {
        var result = new Dictionary<string, object>
        {
            ["_type"] = NodeType,
            ["_name"] = Name,
            ["_elementCount"] = ChildElements.Count,
            ["_attributeCount"] = Attributes.Count
        };

        // 添加元数据（如果存在）
        if (Metadata != null)
        {
            result["_metadata"] = Metadata;
        }

        // 添加子元素
        if (ChildElements.Any())
        {
            result["_elements"] = ChildElements.ToDictionary(
                kvp => kvp.Key, 
                kvp => kvp.Value.ToSerializableObject()
            );
        }

        // 添加属性
        if (Attributes.Any())
        {
            result["_attributes"] = Attributes.ToDictionary(
                kvp => kvp.Key, 
                kvp => kvp.Value.ToSerializableObject()
            );
        }

        return result;
    }

    #region 子元素操作方法

    /// <summary>
    /// 添加子元素
    /// </summary>
    public void AddChildElement(string name, ElementNode element)
    {
        ChildElements[name] = element;
    }

    /// <summary>
    /// 获取子元素
    /// </summary>
    public ElementNode GetChildElement(string name)
    {
        return ChildElements.TryGetValue(name, out var element) ? element : null;
    }

    /// <summary>
    /// 移除子元素
    /// </summary>
    public bool RemoveChildElement(string name)
    {
        return ChildElements.Remove(name);
    }

    /// <summary>
    /// 获取所有子元素名称
    /// </summary>
    public IEnumerable<string> GetChildElementNames()
    {
        return ChildElements.Keys;
    }

    #endregion

    #region 属性操作方法

    /// <summary>
    /// 添加属性节点
    /// </summary>
    public void AddAttribute<TValue>(string name, AttributeNode<TValue> attribute)
    {
        Attributes[name] = attribute;
    }

    /// <summary>
    /// 获取强类型属性
    /// </summary>
    public AttributeNode<TValue> GetAttribute<TValue>(string name)
    {
        return Attributes.TryGetValue(name, out var attr) ? attr as AttributeNode<TValue> : null;
    }

    /// <summary>
    /// 获取属性（基类型）
    /// </summary>
    public TreeNode GetAttribute(string name)
    {
        return Attributes.TryGetValue(name, out var attr) ? attr : null;
    }

    /// <summary>
    /// 移除属性
    /// </summary>
    public bool RemoveAttribute(string name)
    {
        return Attributes.Remove(name);
    }

    /// <summary>
    /// 获取所有属性名称
    /// </summary>
    public IEnumerable<string> GetAttributeNames()
    {
        return Attributes.Keys;
    }

    /// <summary>
    /// 检查是否包含指定属性
    /// </summary>
    public bool HasAttribute(string name)
    {
        return Attributes.ContainsKey(name);
    }

    #endregion

    #region 统计信息

    /// <summary>
    /// 获取元素统计信息
    /// </summary>
    public ElementStatistics GetStatistics()
    {
        return new ElementStatistics
        {
            Name = Name,
            DirectChildElements = ChildElements.Count,
            DirectAttributes = Attributes.Count,
            TotalChildElements = CountTotalChildElements(),
            TotalAttributes = CountTotalAttributes(),
            Depth = CalculateMaxDepth(),
            HasData = Attributes.Any() || ChildElements.Values.Any(e => e.GetStatistics().HasData)
        };
    }

    private int CountTotalChildElements()
    {
        return ChildElements.Count + ChildElements.Values.Sum(e => e.CountTotalChildElements());
    }

    private int CountTotalAttributes()
    {
        return Attributes.Count + ChildElements.Values.Sum(e => e.CountTotalAttributes());
    }

    private int CalculateMaxDepth()
    {
        if (!ChildElements.Any())
            return 1;
        
        return 1 + ChildElements.Values.Max(e => e.CalculateMaxDepth());
    }

    #endregion
}

/// <summary>
/// 属性节点（数据节点，对应AFAttributeEntry）
/// </summary>
public class AttributeNode<TValue> : TreeNode
{
    public override string NodeType => "attribute";
    public TValue Data { get; set; }
    public CacheItemMetadata Metadata { get; set; }

    public override object ToSerializableObject()
    {
        return new Dictionary<string, object>
        {
            ["_type"] = NodeType,
            ["_name"] = Name,
            ["data"] = Data,
            ["metadata"] = Metadata
        };
    }
}

/// <summary>
/// 元素元数据
/// </summary>
public class ElementMetadata
{
    public string Description { get; set; }
    public string Category { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastUpdated { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public Dictionary<string, object> CustomProperties { get; set; } = new Dictionary<string, object>();
}

/// <summary>
/// 元素统计信息
/// </summary>
public class ElementStatistics
{
    public string Name { get; set; }
    public int DirectChildElements { get; set; }
    public int DirectAttributes { get; set; }
    public int TotalChildElements { get; set; }
    public int TotalAttributes { get; set; }
    public int Depth { get; set; }
    public bool HasData { get; set; }
}

/// <summary>
/// 缓存项元数据
/// </summary>
public class CacheItemMetadata
{
    public DateTime CreatedAt { get; set; }
    public DateTime LastReadAt { get; set; }
    public long AccessCount { get; set; }
    public bool IsStale { get; set; }
    public DateTime? LastRefreshedAt { get; set; }
}

/// <summary>
/// 改进的树构建器 - 支持元素和属性的混合结构
/// </summary>
public class ImprovedCacheTreeBuilder<TKey, TValue>
{
    private readonly Func<IEnumerable<TKey>> _getAllKeys;
    private readonly Func<TKey, CacheItem<TValue>> _getCacheItem;
    private readonly Func<TKey, string> _keySerializer;
    private readonly ILogger _logger;

    public ImprovedCacheTreeBuilder(
        Func<IEnumerable<TKey>> getAllKeys,
        Func<TKey, CacheItem<TValue>> getCacheItem,
        Func<TKey, string> keySerializer = null,
        ILogger logger = null)
    {
        _getAllKeys = getAllKeys ?? throw new ArgumentNullException(nameof(getAllKeys));
        _getCacheItem = getCacheItem ?? throw new ArgumentNullException(nameof(getCacheItem));
        _keySerializer = keySerializer ?? (key => key?.ToString() ?? string.Empty);
        _logger = logger;
    }

    /// <summary>
    /// 构建支持混合结构的树
    /// </summary>
    public ElementNode BuildImprovedTree()
    {
        var root = new ElementNode 
        { 
            Name = "Root",
            Metadata = new ElementMetadata
            {
                Description = "AF Cache Tree Root",
                Category = "System",
                CreatedAt = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            }
        };

        var processedCount = 0;
        var skippedCount = 0;

        try
        {
            var allKeys = _getAllKeys().ToList();
            _logger?.Debug("Building improved tree for {KeyCount} cache keys", allKeys.Count);

            foreach (var key in allKeys)
            {
                try
                {
                    var cacheItem = _getCacheItem(key);
                    if (cacheItem?.Value == null)
                    {
                        skippedCount++;
                        continue;
                    }

                    var rawPath = ExtractPathFromKey(key);
                    if (string.IsNullOrEmpty(rawPath))
                    {
                        skippedCount++;
                        continue;
                    }

                    var parsedPath = ParseAFPath(rawPath);
                    if (!parsedPath.IsValid)
                    {
                        _logger?.Warning("Invalid path format for key {Key}: {Path}", key, rawPath);
                        skippedCount++;
                        continue;
                    }

                    InsertIntoImprovedTree(root, parsedPath, cacheItem);
                    processedCount++;
                }
                catch (Exception ex)
                {
                    _logger?.Warning(ex, "Failed to process cache key: {Key}", key);
                    skippedCount++;
                }
            }

            // 更新根节点的统计信息
            root.Metadata.LastUpdated = DateTime.UtcNow;
            root.Metadata.CustomProperties["ProcessedKeys"] = processedCount;
            root.Metadata.CustomProperties["SkippedKeys"] = skippedCount;

            _logger?.Debug("Improved tree build completed: processed={Processed}, skipped={Skipped}", 
                processedCount, skippedCount);

            return root;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to build improved tree");
            return new ElementNode { Name = "Error" };
        }
    }

    private void InsertIntoImprovedTree(ElementNode root, ParsedPath parsedPath, CacheItem<TValue> cacheItem)
    {
        var currentElement = root;

        // 创建或导航到所有元素节点
        foreach (var elementName in parsedPath.ElementPath)
        {
            var childElement = currentElement.GetChildElement(elementName);
            if (childElement == null)
            {
                childElement = new ElementNode 
                { 
                    Name = elementName,
                    Metadata = new ElementMetadata
                    {
                        Description = $"AF Element: {elementName}",
                        Category = "AFElement",
                        CreatedAt = DateTime.UtcNow,
                        LastUpdated = DateTime.UtcNow
                    }
                };
                currentElement.AddChildElement(elementName, childElement);
                _logger?.Debug("Created element node: {ElementName}", elementName);
            }
            else
            {
                // 更新现有元素的最后更新时间
                childElement.Metadata.LastUpdated = DateTime.UtcNow;
            }
            
            currentElement = childElement;
        }

        // 在最终元素下添加属性
        var attributeNode = new AttributeNode<TValue>
        {
            Name = parsedPath.AttributeName,
            Data = cacheItem.Value,
            Metadata = CreateMetadata(cacheItem)
        };

        currentElement.AddAttribute(parsedPath.AttributeName, attributeNode);
        
        _logger?.Debug("Added attribute '{AttributeName}' to element at path: [{ElementPath}]", 
            parsedPath.AttributeName, string.Join("/", parsedPath.ElementPath));
    }

    private ParsedPath ParseAFPath(string rawPath)
    {
        var result = new ParsedPath();

        try
        {
            var pipeIndex = rawPath.LastIndexOf('|');
            
            if (pipeIndex < 0)
            {
                return result; // IsValid = false
            }

            var elementPathStr = rawPath.Substring(0, pipeIndex).Trim();
            var attributeName = rawPath.Substring(pipeIndex + 1).Trim();

            if (string.IsNullOrEmpty(attributeName))
            {
                return result; // IsValid = false
            }

            string[] elementSegments;
            if (!string.IsNullOrEmpty(elementPathStr))
            {
                var normalizedPath = elementPathStr.Replace('\\', '/');
                elementSegments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            }
            else
            {
                elementSegments = Array.Empty<string>();
            }

            result.ElementPath = elementSegments;
            result.AttributeName = attributeName;
            result.IsValid = true;

            return result;
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "Error parsing AF path: {Path}", rawPath);
            return result;
        }
    }

    private string ExtractPathFromKey(TKey key)
    {
        var keyString = _keySerializer(key);
        var pathStartIndex = keyString.LastIndexOf(':');
        
        if (pathStartIndex >= 0 && pathStartIndex < keyString.Length - 1)
        {
            var path = keyString.Substring(pathStartIndex + 1);
            return path.TrimStart('/', '\\');
        }
        
        return keyString;
    }

    private CacheItemMetadata CreateMetadata(CacheItem<TValue> cacheItem)
    {
        return new CacheItemMetadata
        {
            CreatedAt = cacheItem.CreatedAt,
            LastReadAt = cacheItem.LastReadAt,
            AccessCount = System.Threading.Interlocked.Read(ref cacheItem.AccessCount),
            IsStale = cacheItem.IsStale,
            LastRefreshedAt = cacheItem.LastRefreshedAt != DateTime.MinValue ? cacheItem.LastRefreshedAt : null
        };
    }
}

/// <summary>
/// 路径解析结果
/// </summary>
public class ParsedPath
{
    public string[] ElementPath { get; set; } = Array.Empty<string>();
    public string AttributeName { get; set; } = string.Empty;
    public bool IsValid { get; set; }
}

/// <summary>
/// 使用示例
/// </summary>
public class ElementNodeSerializationExample
{
    public static void Example()
    {
        Console.WriteLine("=== ElementNode序列化示例 ===\n");

        // 创建根节点
        var plant = new ElementNode 
        { 
            Name = "Plant1",
            Metadata = new ElementMetadata
            {
                Description = "Production Plant #1",
                Category = "Plant",
                CreatedAt = DateTime.UtcNow,
                Tags = new[] { "production", "chemical" }
            }
        };

        // 创建Unit1
        var unit1 = new ElementNode 
        { 
            Name = "Unit1",
            Metadata = new ElementMetadata
            {
                Description = "Processing Unit 1",
                Category = "Unit"
            }
        };

        // 为Unit1添加属性
        unit1.AddAttribute("Status", new AttributeNode<string>
        {
            Name = "Status",
            Data = "Running",
            Metadata = new CacheItemMetadata
            {
                CreatedAt = DateTime.UtcNow,
                LastReadAt = DateTime.UtcNow,
                AccessCount = 10
            }
        });

        unit1.AddAttribute("Efficiency", new AttributeNode<double>
        {
            Name = "Efficiency",
            Data = 85.5,
            Metadata = new CacheItemMetadata
            {
                CreatedAt = DateTime.UtcNow,
                LastReadAt = DateTime.UtcNow,
                AccessCount = 25
            }
        });

        // 创建Equipment1（Unit1的子元素）
        var equipment1 = new ElementNode { Name = "Equipment1" };
        
        // 为Equipment1添加属性
        equipment1.AddAttribute("Temperature", new AttributeNode<double>
        {
            Name = "Temperature",
            Data = 75.5,
            Metadata = new CacheItemMetadata { AccessCount = 50 }
        });

        equipment1.AddAttribute("Pressure", new AttributeNode<double>
        {
            Name = "Pressure",
            Data = 150.2,
            Metadata = new CacheItemMetadata { AccessCount = 30 }
        });

        // 构建层次结构
        unit1.AddChildElement("Equipment1", equipment1);
        plant.AddChildElement("Unit1", unit1);

        // 序列化到JSON
        var serialized = plant.ToSerializableObject();
        var jsonString = JsonConvert.SerializeObject(serialized, Formatting.Indented);
        
        Console.WriteLine("序列化结果:");
        Console.WriteLine(jsonString);

        Console.WriteLine("\n=== 统计信息 ===");
        var stats = plant.GetStatistics();
        Console.WriteLine($"Plant统计: 直接子元素={stats.DirectChildElements}, 直接属性={stats.DirectAttributes}");
        Console.WriteLine($"         总子元素={stats.TotalChildElements}, 总属性={stats.TotalAttributes}");
        Console.WriteLine($"         最大深度={stats.Depth}, 包含数据={stats.HasData}");
    }
}

// CacheItem定义（为了完整性）
public class CacheItem<T>
{
    public T Value { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastReadAt { get; set; }
    public long AccessCount;
    public bool IsStale { get; set; } = false;
    public DateTime LastRefreshedAt { get; set; } = DateTime.MinValue;
}

// ILogger接口（简化版本）
public interface ILogger
{
    void Debug(string message, params object[] args);
    void Warning(string message, params object[] args);
    void Error(Exception ex, string message, params object[] args);
}