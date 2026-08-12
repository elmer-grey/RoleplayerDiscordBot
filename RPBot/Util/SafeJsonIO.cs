using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RPBot.Util
{
    /// <summary>
    /// Утилиты безопасной записи JSON-сторей.
    /// Даёт три вещи:
    ///   1. <see cref="WriteAtomicAsync"/> — запись через временный .tmp + File.Move(overwrite),
    ///      чтобы при падении процесса старый файл остался валидным.
    ///   2. <see cref="AcquireLock"/> / <see cref="AcquireLockAsync"/> — межпроцессная
    ///      блокировка через <c>FileStream</c> с <c>FileShare.None</c> на sidecar-файл
    ///      "<name>.lock". Используется как advisory mutex для защиты от потери
    ///      обновлений при запуске двух инстансов бота с одним RPBOT_DATA_DIR.
    ///   3. <see cref="TryReadShared"/> — открывает файл с общим доступом,
    ///      чтобы запись в другом процессе не блокировала чтение.
    /// </summary>
    public static class SafeJsonIO
    {
        private const int DefaultLockRetries = 20;
        private const int DefaultLockRetryDelayMs = 100;

        /// <summary>
        /// Записывает JSON-текст в <paramref name="targetPath"/> атомарно:
        /// сначала во временный файл ".tmp" рядом, затем <c>File.Move(..., overwrite:true)</c>.
        /// При ошибке временный файл удаляется, основной остаётся прежним.
        /// </summary>
        public static async Task WriteAtomicAsync(string targetPath, string json, CancellationToken ct = default)
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = targetPath + ".tmp";
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                await using (var fs = new FileStream(
                    tmpPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true))
                {
                    await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                }

                // File.Move с overwrite — атомарно подменяет основной файл
                // (на NTFS — через MoveFileEx с MOVEFILE_REPLACE_EXISTING).
                File.Move(tmpPath, targetPath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Синхронный вариант — для мест, где async неудобен (конструкторы, lock-блоки).
        /// </summary>
        public static void WriteAtomic(string targetPath, string json)
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = targetPath + ".tmp";
            try
            {
                File.WriteAllText(tmpPath, json, new System.Text.UTF8Encoding(false));
                File.Move(tmpPath, targetPath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Пытается взять межпроцессную блокировку через sidecar-файл
        /// "<paramref name="targetPath"/>.lock" с <c>FileShare.None</c>.
        /// Возвращает <c>null</c>, если не удалось за <paramref name="retries"/> попыток
        /// (другой процесс держит файл).
        /// Полученный <see cref="FileStream"/> нужно dispose'ить — это и есть release.
        /// </summary>
        public static FileStream? AcquireLock(
            string targetPath,
            int retries = DefaultLockRetries,
            int retryDelayMs = DefaultLockRetryDelayMs)
        {
            var lockPath = targetPath + ".lock";
            var dir = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            for (int attempt = 0; attempt < retries; attempt++)
            {
                try
                {
                    return new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
                catch (IOException) when (attempt + 1 < retries)
                {
                    Thread.Sleep(retryDelayMs);
                }
                catch (UnauthorizedAccessException) when (attempt + 1 < retries)
                {
                    Thread.Sleep(retryDelayMs);
                }
            }
            return null;
        }

        /// <summary>Async-вариант межпроцессной блокировки.</summary>
        public static async Task<FileStream?> AcquireLockAsync(
            string targetPath,
            int retries = DefaultLockRetries,
            int retryDelayMs = DefaultLockRetryDelayMs,
            CancellationToken ct = default)
        {
            var lockPath = targetPath + ".lock";
            var dir = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            for (int attempt = 0; attempt < retries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
                catch (IOException) when (attempt + 1 < retries)
                {
                    await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (attempt + 1 < retries)
                {
                    await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
                }
            }
            return null;
        }

        /// <summary>
        /// Читает файл с общим доступом (FileShare.ReadWrite) — пишущий процесс не блокирует читателя.
        /// </summary>
        public static string? TryReadShared(string targetPath)
        {
            try
            {
                if (!File.Exists(targetPath)) return null;
                using var fs = new FileStream(
                    targetPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
                return sr.ReadToEnd();
            }
            catch
            {
                return null;
            }
        }
    }
}