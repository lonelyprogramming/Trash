// 1. 服务接口定义
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace YourProject.Services
{
    public interface IDataService
    {
        Task<object> GetItemByIdAsync(int id);
        Task<IEnumerable<object>> SearchAsync(string keyword, int page, int pageSize);
        Task<IEnumerable<object>> GetFilteredDataAsync(FilterRequest filter);
        Task<object> GetStatisticsAsync();
    }

    public interface ILoggingService
    {
        void LogInfo(string message);
        void LogError(string message, Exception exception);
        void LogWarning(string message);
    }

    public interface ICacheService
    {
        Task<T> GetAsync<T>(string key);
        Task SetAsync<T>(string key, T value, TimeSpan? expiration = null);
        Task RemoveAsync(string key);
    }
}

// 2. 服务实现
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace YourProject.Services
{
    public class DataService : IDataService
    {
        private readonly ILoggingService _logger;
        private readonly ICacheService _cache;

        public DataService(ILoggingService logger, ICacheService cache)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public async Task<object> GetItemByIdAsync(int id)
        {
            _logger.LogInfo($"Getting item by ID: {id}");
            
            string cacheKey = $"item_{id}";
            var cachedItem = await _cache.GetAsync<object>(cacheKey);
            
            if (cachedItem != null)
            {
                _logger.LogInfo($"Item {id} found in cache");
                return cachedItem;
            }

            // 模拟数据库查询
            await Task.Delay(100);
            
            var item = new
            {
                Id = id,
                Name = $"Item {id}",
                Value = id * 10.5,
                Description = $"This is item number {id}",
                CreatedDate = DateTime.UtcNow.AddDays(-id)
            };

            // 缓存结果
            await _cache.SetAsync(cacheKey, item, TimeSpan.FromMinutes(5));
            
            _logger.LogInfo($"Item {id} loaded from data source and cached");
            return item;
        }

        public async Task<IEnumerable<object>> SearchAsync(string keyword, int page, int pageSize)
        {
            _logger.LogInfo($"Searching for: {keyword}, page: {page}, pageSize: {pageSize}");
            
            // 模拟搜索延迟
            await Task.Delay(50);
            
            var searchResults = new List<object>();
            
            for (int i = 1; i <= pageSize; i++)
            {
                searchResults.Add(new
                {
                    Id = (page - 1) * pageSize + i,
                    Title = $"{keyword} Result {i}",
                    Description = $"Search result for '{keyword}' on page {page}",
                    Score = 100 - i
                });
            }

            return searchResults;
        }

        public async Task<IEnumerable<object>> GetFilteredDataAsync(FilterRequest filter)
        {
            _logger.LogInfo("Getting filtered data");
            
            await Task.Delay(75);
            
            var data = new List<object>();
            int count = filter?.MaxResults ?? 20;
            string category = filter?.Category ?? "General";
            DateTime fromDate = filter?.FromDate ?? DateTime.UtcNow.AddDays(-30);
            DateTime toDate = filter?.ToDate ?? DateTime.UtcNow;

            for (int i = 1; i <= count; i++)
            {
                data.Add(new
                {
                    Id = i,
                    Name = $"{category} Item {i}",
                    Category = category,
                    Price = Math.Round(50 + (i * 10.5), 2),
                    Date = fromDate.AddDays(i % (int)(toDate - fromDate).TotalDays),
                    IsActive = i % 2 == 0,
                    Tags = new[] { category.ToLower(), $"tag{i}", "sample" }
                });
            }

            return data;
        }

        public async Task<object> GetStatisticsAsync()
        {
            _logger.LogInfo("Getting system statistics");
            
            await Task.Delay(25);
            
            return new
            {
                Server = new
                {
                    Name = Environment.MachineName,
                    OS = Environment.OSVersion.ToString(),
                    ProcessorCount = Environment.ProcessorCount,
                    WorkingSet = Environment.WorkingSet,
                    UpTime = TimeSpan.FromMilliseconds(Environment.TickCount)
                },
                Application = new
                {
                    Version = "1.0.0",
                    StartTime = DateTime.UtcNow.AddHours(-2),
                    RequestsProcessed = 1250,
                    ActiveConnections = 45
                }
            };
        }
    }

    public class LoggingService : ILoggingService
    {
        public void LogInfo(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[INFO] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
        }

        public void LogError(string message, Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[ERROR] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
            if (exception != null)
                System.Diagnostics.Debug.WriteLine($"Exception: {exception}");
        }

        public void LogWarning(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[WARNING] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
        }
    }

    public class MemoryCacheService : ICacheService
    {
        private static readonly Dictionary<string, (object Value, DateTime Expiration)> _cache 
            = new Dictionary<string, (object, DateTime)>();
        private static readonly object _lock = new object();

        public Task<T> GetAsync<T>(string key)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var item))
                {
                    if (item.Expiration > DateTime.UtcNow)
                    {
                        return Task.FromResult((T)item.Value);
                    }
                    else
                    {
                        _cache.Remove(key);
                    }
                }
                return Task.FromResult(default(T));
            }
        }

        public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null)
        {
            lock (_lock)
            {
                var exp = DateTime.UtcNow.Add(expiration ?? TimeSpan.FromMinutes(30));
                _cache[key] = (value, exp);
            }
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key)
        {
            lock (_lock)
            {
                _cache.Remove(key);
            }
            return Task.CompletedTask;
        }
    }
}

// 3. 改进后的Controller
using System;
using System.Threading.Tasks;
using System.Web.Http;
using Newtonsoft.Json;
using YourProject.Services;

namespace YourProject.Controllers
{
    [RoutePrefix("api")]
    public class DataController : ApiController
    {
        private readonly IDataService _dataService;
        private readonly ILoggingService _logger;

        public DataController(IDataService dataService, ILoggingService logger)
        {
            _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpGet]
        [Route("get")]
        public IHttpActionResult Get()
        {
            try
            {
                _logger.LogInfo("Processing GET request");
                
                var responseData = new
                {
                    Message = "Hello from Web API with DI",
                    Timestamp = DateTime.UtcNow,
                    Status = "Success"
                };

                return Ok(responseData);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error in Get method", ex);
                return InternalServerError(ex);
            }
        }

        [HttpGet]
        [Route("get/{id:int}")]
        public async Task<IHttpActionResult> GetById(int id)
        {
            try
            {
                var item = await _dataService.GetItemByIdAsync(id);
                
                return Ok(new
                {
                    Success = true,
                    Data = item,
                    Message = $"Successfully retrieved item {id}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting item {id}", ex);
                return InternalServerError(ex);
            }
        }

        [HttpGet]
        [Route("get/search")]
        public async Task<IHttpActionResult> Search(string keyword = "", int page = 1, int pageSize = 10)
        {
            try
            {
                var results = await _dataService.SearchAsync(keyword, page, pageSize);
                
                var response = new
                {
                    Query = keyword,
                    Page = page,
                    PageSize = pageSize,
                    Results = results,
                    TotalResults = 100,
                    HasNextPage = page * pageSize < 100
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in search: {keyword}", ex);
                return InternalServerError(ex);
            }
        }

        [HttpPost]
        [Route("get/filter")]
        public async Task<IHttpActionResult> GetWithFilter()
        {
            try
            {
                string requestBody = await Request.Content.ReadAsStringAsync();
                
                FilterRequest filter = null;
                if (!string.IsNullOrEmpty(requestBody))
                {
                    filter = JsonConvert.DeserializeObject<FilterRequest>(requestBody);
                }

                var filteredData = await _dataService.GetFilteredDataAsync(filter);

                var response = new
                {
                    Applied_Filter = filter,
                    ResultCount = System.Linq.Enumerable.Count(filteredData),
                    Data = filteredData,
                    Timestamp = DateTime.UtcNow
                };

                return Ok(response);
            }
            catch (JsonException ex)
            {
                _logger.LogError("Invalid JSON in request body", ex);
                return BadRequest($"Invalid JSON in request body: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError("Error in GetWithFilter", ex);
                return InternalServerError(ex);
            }
        }

        [HttpGet]
        [Route("get/stats")]
        public async Task<IHttpActionResult> GetStatistics()
        {
            try
            {
                var stats = await _dataService.GetStatisticsAsync();
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting statistics", ex);
                return InternalServerError(ex);
            }
        }
    }

    public class FilterRequest
    {
        public string Category { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public int? MaxResults { get; set; }
        public decimal? MinPrice { get; set; }
        public decimal? MaxPrice { get; set; }
        public bool? IsActive { get; set; }
        public string[] Tags { get; set; }
    }
}

// 4. Unity DI 容器配置
using System.Web.Http;
using Unity;
using Unity.WebApi;
using YourProject.Services;

namespace YourProject
{
    public static class UnityConfig
    {
        public static void RegisterComponents()
        {
            var container = new UnityContainer();

            // 注册服务
            container.RegisterType<ILoggingService, LoggingService>();
            container.RegisterType<ICacheService, MemoryCacheService>();
            container.RegisterType<IDataService, DataService>();

            // 设置依赖解析器
            GlobalConfiguration.Configuration.DependencyResolver = new UnityDependencyResolver(container);
        }
    }
}

// 5. Global.asax.cs 中的启动配置
using System.Web.Http;

namespace YourProject
{
    public class WebApiApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            // 配置 Web API
            GlobalConfiguration.Configure(WebApiConfig.Register);
            
            // 注册依赖注入
            UnityConfig.RegisterComponents();
        }
    }
}

// 6. WebApiConfig.cs
using System.Web.Http;

namespace YourProject
{
    public static class WebApiConfig
    {
        public static void Register(HttpConfiguration config)
        {
            // Web API 配置和服务

            // Web API 路由
            config.MapHttpAttributeRoutes();

            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "api/{controller}/{id}",
                defaults: new { id = RouteParameter.Optional }
            );

            // 配置 JSON 序列化
            config.Formatters.JsonFormatter.SerializerSettings.DateFormatHandling 
                = Newtonsoft.Json.DateFormatHandling.IsoDateFormat;
            config.Formatters.JsonFormatter.SerializerSettings.NullValueHandling 
                = Newtonsoft.Json.NullValueHandling.Ignore;
        }
    }
}