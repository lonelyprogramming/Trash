// Global.asax.cs
protected void Application_Start()
{
    WebApiConfig.Register(GlobalConfiguration.Configuration);
    ConfigureDependencyInjection();
}

private void ConfigureDependencyInjection()
{
    var container = new Container();
    container.Options.DefaultScopedLifestyle = new WebApiRequestLifestyle();
    
    container.Register<ILogger, ConsoleLogger>(Lifestyle.Singleton);
    container.Register<ICacheDataService, MockCacheDataService>(Lifestyle.Scoped);
    
    container.Register<ImprovedCacheTreeBuilder<string, object>>(() =>
    {
        var cacheService = container.GetInstance<ICacheDataService>();
        var logger = container.GetInstance<ILogger>();
        
        return new ImprovedCacheTreeBuilder<string, object>(
            () => cacheService.GetAllKeys(),
            key => cacheService.GetCacheItem(key),
            key => key,
            logger
        );
    }, Lifestyle.Scoped);
    
    container.Verify();
    GlobalConfiguration.Configuration.DependencyResolver = new SimpleInjectorWebApiDependencyResolver(container);
}

// ElementTreeController.cs
[RoutePrefix("api/elementtree")]
public class ElementTreeController : ApiController
{
    private readonly ImprovedCacheTreeBuilder<string, object> _treeBuilder;
    
    public ElementTreeController(ImprovedCacheTreeBuilder<string, object> treeBuilder)
    {
        _treeBuilder = treeBuilder;
    }
    
    [HttpGet]
    [Route("build")]
    public IHttpActionResult BuildTree()
    {
        try
        {
            var tree = _treeBuilder.BuildImprovedTree();
            var serializedTree = tree.ToSerializableObject();
            return Ok(serializedTree);
        }
        catch (Exception ex)
        {
            return InternalServerError(ex);
        }
    }
    
    [HttpPost]
    [Route("query")]
    public IHttpActionResult QueryElement([FromBody]ElementQueryRequest request)
    {
        if (request == null || string.IsNullOrEmpty(request.Path))
        {
            return BadRequest("Path is required");
        }
        
        try
        {
            var tree = _treeBuilder.BuildImprovedTree();
            var element = tree.FindElement(request.Path);
            
            if (element == null)
            {
                return NotFound();
            }
            
            var result = new
            {
                Element = element.ToSerializableObject(),
                Statistics = element.GetStatistics(),
                AllAttributes = !string.IsNullOrEmpty(request.AttributeFilter) ? 
                    element.GetAllAttributes().Where(a => a.Key.Contains(request.AttributeFilter)).ToList() :
                    element.GetAllAttributes().ToList()
            };
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            return InternalServerError(ex);
        }
    }
}

// Supporting classes
public class ElementQueryRequest
{
    public string Path { get; set; }
    public string AttributeFilter { get; set; }
}

public class ConsoleLogger : ILogger
{
    public void Debug(string message, params object[] args)
    {
        System.Diagnostics.Debug.WriteLine($"[DEBUG] {string.Format(message, args)}");
    }
    
    public void Warning(string message, params object[] args)
    {
        System.Diagnostics.Debug.WriteLine($"[WARNING] {string.Format(message, args)}");
    }
    
    public void Error(Exception ex, string message, params object[] args)
    {
        System.Diagnostics.Debug.WriteLine($"[ERROR] {string.Format(message, args)} - {ex}");
    }
}

public interface ICacheDataService
{
    IEnumerable<string> GetAllKeys();
    CacheItem<object> GetCacheItem(string key);
}

public class MockCacheDataService : ICacheDataService
{
    public IEnumerable<string> GetAllKeys()
    {
        return new[]
        {
            "cache:Plant1/Unit1|Status",
            "cache:Plant1/Unit1|Efficiency", 
            "cache:Plant1/Unit1/Equipment1|Temperature",
            "cache:Plant1/Unit1/Equipment1|Pressure"
        };
    }
    
    public CacheItem<object> GetCacheItem(string key)
    {
        var data = key switch
        {
            "cache:Plant1/Unit1|Status" => "Running",
            "cache:Plant1/Unit1|Efficiency" => 85.5,
            "cache:Plant1/Unit1/Equipment1|Temperature" => 75.5,
            "cache:Plant1/Unit1/Equipment1|Pressure" => 150.2,
            _ => null
        };
        
        return data != null ? new CacheItem<object>
        {
            Value = data,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            LastReadAt = DateTime.UtcNow,
            AccessCount = new Random().Next(1, 100),
            IsStale = false
        } : null;
    }
}