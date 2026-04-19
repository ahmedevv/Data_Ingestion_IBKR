using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;

namespace API
{
    public class DiskWriter
    {
        private readonly string _csvFolder;
        private readonly Action<string, string, Color> _logger;
        private readonly string _csvHeader;

        public DiskWriter(string csvFolder, string csvHeader, Action<string, string, Color> logger)
        {
            _csvFolder = csvFolder;
            _csvHeader = csvHeader;
            _logger = logger;

            if (!Directory.Exists(_csvFolder))
            {
                Directory.CreateDirectory(_csvFolder);
                _logger?.Invoke("IO-DIR", $"Created missing directory path: {_csvFolder}", Color.Gray);
            }
        }

        public void Flush(ConcurrentDictionary<int, StockData> stockHash)
        {
            _logger?.Invoke("IO-START", "60-Second threshold reached. Extracting buffer to Disk...", Color.Cyan);
            int totalBarsSaved = 0;
            int filesTouched = 0;

            Task.Run(() => {
                foreach (var stock in stockHash.Values)
                {
                    if (stock.BarBuffer.IsEmpty) continue;

                    List<string> linesToWrite = new List<string>();
                    while (stock.BarBuffer.TryDequeue(out string line))
                    {
                        linesToWrite.Add(line);
                    }

                    try
                    {
                        string path = Path.Combine(_csvFolder, $"{stock.Symbol}.csv");
                        if (!File.Exists(path))
                        {
                            _logger?.Invoke("IO-CREATE", $"Creating new CSV matrix for {stock.Symbol}", Color.DarkGray);
                            File.WriteAllText(path, _csvHeader);
                        }

                        File.AppendAllLines(path, linesToWrite);
                        totalBarsSaved += linesToWrite.Count;
                        filesTouched++;
                    }
                    catch (IOException ex)
                    {
                        // File locked resilience - do not lose data
                        foreach (var line in linesToWrite) stock.BarBuffer.Enqueue(line);
                        _logger?.Invoke("IO-LOCK", $"File locked for {stock.Symbol}. Re-queued {linesToWrite.Count} lines. {ex.Message}", Color.Orange);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Invoke("IO-FATAL", $"Critical write failure for {stock.Symbol}: {ex.Message}", Color.Red);
                    }
                }

                if (totalBarsSaved > 0)
                    _logger?.Invoke("FLUSH-DONE", $"Buffer cleared. Safely wrote {totalBarsSaved} rows across {filesTouched} CSVs.", Color.LimeGreen);
                else
                    _logger?.Invoke("FLUSH-EMPTY", "Buffer empty. No initialized data recorded this cycle.", Color.DarkGray);
            });
        }
    }
}