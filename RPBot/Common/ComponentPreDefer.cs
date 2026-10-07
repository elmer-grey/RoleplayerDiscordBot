using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Discord.WebSocket;

namespace RPBot.Common
{
    /// <summary>
    /// Универсальный helper для <c>DeferAsync</c> компонентов (кнопок/SelectMenu) с
    /// защитой от типичных сетевых сбоев:
    /// <list type="bullet">
    ///   <item>10062 Unknown interaction (после Gateway Reconnect / рестарта бота) — до 2 ретраев с паузой 150/350мс.</item>
    ///   <item>SSL/IO/socket исключения — тоже ретраятся.</item>
    ///   <item>SLOW-детектор: если первая попытка &gt;100мс — лог с gcPause/gen счётчиками (как в slash PreDefer).</item>
    /// </list>
    /// <para>Возвращает <c>true</c> если DeferAsync успешен (можно делать Followup).
    /// При неудаче возвращает <c>false</c>, обработчик должен <c>return</c>.</para>
    /// </summary>
    internal static class ComponentPreDefer
    {
        /// <summary>Префикс имени для логов (например "pred_bet").</summary>
        public static async Task<bool> TryDeferAsync(
            SocketMessageComponent component,
            string commandName,
            bool ephemeral = true)
        {
            bool deferred = false;
            Exception? lastEx = null;
            for (int attempt = 0; attempt <= 2 && !deferred; attempt++)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    await component.DeferAsync(ephemeral: ephemeral);
                    sw.Stop();
                    deferred = true;
                    if (attempt > 0)
                    {
                        BotLogger.Info(LogCategory.Cmd,
                            $"[{commandName}:DeferAsync] succeeded на попытке {attempt + 1}/3 для interaction={component.Id} (был 10062 ранее).");
                    }
                    else if (sw.Elapsed.TotalMilliseconds > 100)
                    {
                        long totalPauseMs = (long)GC.GetTotalPauseDuration().TotalMilliseconds;
                        int gen0 = GC.CollectionCount(0);
                        int gen1 = GC.CollectionCount(1);
                        int gen2 = GC.CollectionCount(2);
                        long heapMB = (long)(GC.GetTotalMemory(false) / 1024d / 1024d);
                        BotLogger.Warn(LogCategory.Cmd,
                            $"[{commandName}:DeferAsync] SLOW attempt=1/3 took={sw.Elapsed.TotalMilliseconds:F0}ms " +
                            $"interaction={component.Id} " +
                            $"gcPause={totalPauseMs}ms gen0={gen0} gen1={gen1} gen2={gen2} heap={heapMB:F1}MB " +
                            $"— возможна IO/GC пауза.");
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    bool isHttpNotFound = ex is Discord.Net.HttpException httpEx
                        && httpEx.HttpCode == System.Net.HttpStatusCode.NotFound;
                    bool isSslBroken = ex is HttpRequestException httpReq
                        && (httpReq.InnerException is IOException
                            || httpReq.InnerException is SocketException
                            || (httpReq.InnerException?.Message?.Contains("SSL") ?? false)
                            || (httpReq.Message?.Contains("SSL") ?? false));
                    bool retriable = isHttpNotFound || isSslBroken;
                    if (!retriable || attempt == 2)
                    {
                        DeferFailureLogger.Log(commandName, ex, component, component.Data.CustomId);
                        return false;
                    }
                    var delayMs = attempt == 0 ? 150 : 350;
                    try { await Task.Delay(delayMs); } catch { }
                }
            }
            return deferred;
        }
    }
}