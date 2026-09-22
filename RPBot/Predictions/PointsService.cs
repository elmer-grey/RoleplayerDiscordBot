using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RPBot.Util;

namespace RPBot
{
    /// <summary>
    /// Хранит и обновляет баланс костяшек (по гильдии и пользователю) с сохранением в файл.
    /// </summary>
    public class PointsService
    {
        private readonly string _statePath;
        private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, long>> _balances = new();
        private readonly SemaphoreSlim _ioLock = new(1, 1);

        public PointsService(string statePath)
        {
            _statePath = statePath;
        }

        public long GetBalance(ulong guildId, ulong userId)
        {
            if (_balances.TryGetValue(guildId, out var guildBalances) &&
                guildBalances.TryGetValue(userId, out var value))
            {
                return value;
            }

            return 0;
        }

        public bool TrySpend(ulong guildId, ulong userId, long amount)
        {
            if (amount <= 0) return false;

            var guildBalances = _balances.GetOrAdd(guildId, _ => new ConcurrentDictionary<ulong, long>());

            while (true)
            {
                var current = guildBalances.GetOrAdd(userId, 0);
                if (current < amount)
                    return false;

                var newValue = current - amount;
                if (guildBalances.TryUpdate(userId, newValue, current))
                    return true;
            }
        }

        public void Add(ulong guildId, ulong userId, long amount)
        {
            if (amount == 0) return;

            var guildBalances = _balances.GetOrAdd(guildId, _ => new ConcurrentDictionary<ulong, long>());
            guildBalances.AddOrUpdate(userId, amount, (_, current) => current + amount);
        }

        public async Task LoadAsync()
        {
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(_statePath))
                    return;

                var json = await File.ReadAllTextAsync(_statePath).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                    return;

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var raw = JsonSerializer.Deserialize<Dictionary<ulong, Dictionary<ulong, long>>>(json, options);
                if (raw == null)
                    return;

                // Строим новый снимок, и только затем подменяем _balances
                // атомарно — иначе при исключении ниже мы бы оставили
                // _balances пустым и потеряли все начисленные костяшки.
                var newBalances = new ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, long>>();
                foreach (var g in raw)
                {
                    var inner = new ConcurrentDictionary<ulong, long>(g.Value);
                    newBalances[g.Key] = inner;
                }

                _balances.Clear();
                foreach (var kv in newBalances)
                    _balances[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Points,
                    $"[PointsService] failed to load '{_statePath}': {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _ioLock.Release();
            }
        }

        public Dictionary<ulong, Dictionary<ulong, long>> GetSnapshot()
        {
            var snapshot = new Dictionary<ulong, Dictionary<ulong, long>>();
            foreach (var g in _balances)
            {
                snapshot[g.Key] = new Dictionary<ulong, long>(g.Value);
            }
            return snapshot;
        }

        public async Task SaveAsync()
        {
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var dir = Path.GetDirectoryName(_statePath) ?? AppContext.BaseDirectory;
                Directory.CreateDirectory(dir);

                var snapshot = new Dictionary<ulong, Dictionary<ulong, long>>();
                foreach (var g in _balances)
                {
                    snapshot[g.Key] = new Dictionary<ulong, long>(g.Value);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(snapshot, options);
                var tmpPath = _statePath + ".tmp";
                await File.WriteAllTextAsync(tmpPath, json).ConfigureAwait(false);
                File.Move(tmpPath, _statePath, overwrite: true);
            }
            catch (Exception ex)
            {
                BotLogger.Error(LogCategory.Points,
                    $"[PointsService] failed to save '{_statePath}': {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _ioLock.Release();
            }
        }
    }
}
