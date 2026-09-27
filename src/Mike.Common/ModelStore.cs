using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Mike.Common
{
    public class ModelMetadata
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty; // e.g., "LlamaCpp", "Ollama"
        public string Version { get; set; } = string.Empty;
        public Dictionary<string, string> Config { get; set; } = new Dictionary<string, string>();
    }

    public class ModelStore
    {
        private readonly string _storePath;
        private List<ModelMetadata> _models = new List<ModelMetadata>();
        private const string ConfigFile = "models.json";

        public ModelStore(string storePath)
        {
            _storePath = storePath;
            if (!Directory.Exists(_storePath))
            {
                Directory.CreateDirectory(_storePath);
            }
            LoadConfig();
        }

        private void LoadConfig()
        {
            string path = Path.Combine(_storePath, ConfigFile);
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    _models = JsonSerializer.Deserialize<List<ModelMetadata>>(json) ?? new List<ModelMetadata>();
                }
                catch (Exception)
                {
                    _models = new List<ModelMetadata>();
                }
            }
        }

        public void SaveConfig()
        {
            string path = Path.Combine(_storePath, ConfigFile);
            string json = JsonSerializer.Serialize(_models, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public void RegisterModel(ModelMetadata model)
        {
            var existing = _models.FirstOrDefault(m => m.Id == model.Id);
            if (existing != null)
            {
                _models.Remove(existing);
            }
            _models.Add(model);
            SaveConfig();
        }

        public ModelMetadata? GetModel(string id)
        {
            return _models.FirstOrDefault(m => m.Id == id);
        }

        public List<ModelMetadata> ListModels()
        {
            return _models;
        }

        public ModelMetadata? GetDefaultModel(string provider)
        {
            return _models.FirstOrDefault(m => m.Provider == provider);
        }
    }
}
