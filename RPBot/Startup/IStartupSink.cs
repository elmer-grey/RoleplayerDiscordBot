using System.Threading;
using System.Threading.Tasks;

namespace RPBot.Startup
{
    public interface IStartupSink
    {
        Task WriteAsync(StartupLogRecord record, CancellationToken ct);
    }
}
