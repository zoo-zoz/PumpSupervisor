namespace PumpSupervisor.Domain.Configuration
{
    public class ApiSettings
    {
        public int Port { get; set; } = 5100;
        public bool EnableSwagger { get; set; } = true;
        public bool EnableCors { get; set; } = true;
        public string SwaggerTitle { get; set; } = "泵控系统 API";
        public string SwaggerVersion { get; set; } = "v1";
        public string? SwaggerDescription { get; set; }
        public string? ContactName { get; set; }
        public string? ContactEmail { get; set; }
    }
}