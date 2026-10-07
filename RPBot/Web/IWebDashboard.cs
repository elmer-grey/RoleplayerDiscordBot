using System;
using System.Threading.Tasks;

namespace RPBot.Web
{
    /// <summary>
    /// Общий интерфейс для двух реализаций web-дашборда:
    /// - WebDashboardService (HttpListener, Windows-only)
    /// - WebDashboardHost   (Kestrel, Linux-only)
    ///
    /// Program.cs выбирает реализацию по OperatingSystem.IsWindows(), а всё остальное
    /// обращается только к этому интерфейсу — без условной логики.
    /// </summary>
    public interface IWebDashboard : IDisposable
    {
        /// <summary>Запускает HTTP-сервер на заданном при создании host:port.</summary>
        void Start();

        /// <summary>Корректно останавливает сервер (отменяет request loop, освобождает сокеты).</summary>
        Task StopAsync();
    }
}