using System;
using System.IO;
using System.Text.Json;

namespace CrxMem.Core
{
    public sealed class JsonTableSerializer : ITableSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public string Serialize(CheatTable table)
        {
            ArgumentNullException.ThrowIfNull(table);
            table.Format = CheatTable.NativeFormat;
            table.Version = CheatTable.CurrentVersion;
            return JsonSerializer.Serialize(table, Options);
        }

        public CheatTable Deserialize(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidDataException("The table file is empty.");

            CheatTable? table;
            try
            {
                table = JsonSerializer.Deserialize<CheatTable>(content, Options);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The file is not a valid CrxMem JSON table.", ex);
            }

            if (table == null)
                throw new InvalidDataException("The table file did not contain a table object.");

            if (!string.Equals(table.Format, CheatTable.NativeFormat, StringComparison.Ordinal))
                throw new InvalidDataException($"Unsupported table format '{table.Format}'.");

            table.Entries ??= [];
            return table;
        }

        public void Save(string path, CheatTable table)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temporaryPath = fullPath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, Serialize(table));
                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        public CheatTable Load(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            return Deserialize(File.ReadAllText(path));
        }
    }
}
