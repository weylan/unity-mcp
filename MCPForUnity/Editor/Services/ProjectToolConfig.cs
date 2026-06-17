using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    public sealed class ProjectToolConfig : IDisposable
    {
        private const string Description = "MCP for Unity per-tool enable map. Managed by Unity Editor MCP Tools window or Tools/mcp_tools_config.js. Unlisted tools fall back to group defaults.";
        private static readonly Lazy<ProjectToolConfig> LazyInstance = new(() => new ProjectToolConfig());

        private readonly object syncRoot = new();
        private readonly Dictionary<string, bool> tools = new(StringComparer.Ordinal);
        private FileSystemWatcher watcher;
        private string configPath;
        private bool disposed;

        public static ProjectToolConfig Instance => LazyInstance.Value;

        public event Action OnReloaded;

        public string ConfigPath
        {
            get
            {
                lock (syncRoot)
                {
                    return configPath;
                }
            }
        }

        private ProjectToolConfig()
        {
            LoadOrDefault();
        }

        public void LoadOrDefault()
        {
            string nextPath = ResolveConfigPath();
            var nextTools = new Dictionary<string, bool>(StringComparer.Ordinal);

            if (File.Exists(nextPath))
            {
                try
                {
                    var root = JObject.Parse(File.ReadAllText(nextPath));
                    if (root["tools"] is JObject toolMap)
                    {
                        foreach (var prop in toolMap.Properties())
                        {
                            if (prop.Value.Type == JTokenType.Boolean)
                            {
                                nextTools[prop.Name] = prop.Value.Value<bool>();
                            }
                        }
                    }
                    McpLog.Info($"Loaded project tool config: {nextPath} ({nextTools.Count} overrides)", false);
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"Failed to load project tool config '{nextPath}': {ex.Message}");
                }
            }

            lock (syncRoot)
            {
                configPath = nextPath;
                tools.Clear();
                foreach (var kvp in nextTools)
                {
                    tools[kvp.Key] = kvp.Value;
                }
            }

            ConfigureWatcher(nextPath);
        }

        public bool TryGet(string toolName, out bool enabled)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                enabled = false;
                return false;
            }

            lock (syncRoot)
            {
                return tools.TryGetValue(toolName, out enabled);
            }
        }

        public bool HasOverride(string toolName)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                return false;
            }

            lock (syncRoot)
            {
                return tools.ContainsKey(toolName);
            }
        }

        public void Set(string toolName, bool enabled)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                return;
            }

            lock (syncRoot)
            {
                tools[toolName] = enabled;
            }
        }

        public void Save()
        {
            string pathToWrite;
            JObject root;
            lock (syncRoot)
            {
                pathToWrite = configPath ?? ResolveConfigPath();
                root = new JObject
                {
                    ["version"] = 1,
                    ["description"] = Description,
                    ["tools"] = new JObject(
                        tools
                            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                            .Select(kvp => new JProperty(kvp.Key, kvp.Value)))
                };
            }

            string directory = Path.GetDirectoryName(pathToWrite);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            McpConfigurationHelper.WriteAtomicFile(
                pathToWrite,
                root.ToString(Formatting.Indented) + Environment.NewLine);
        }

        public string GetSource(string toolName)
        {
            return HasOverride(toolName) ? "project-config" : "editor-prefs";
        }

        public void Dispose()
        {
            disposed = true;
            watcher?.Dispose();
            watcher = null;
        }

        private static string ResolveConfigPath()
        {
            string dataPath = Application.dataPath;
            string current = string.IsNullOrEmpty(dataPath)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(dataPath);

            while (!string.IsNullOrEmpty(current))
            {
                string config = Path.Combine(current, EditorPrefKeys.ProjectConfigFileName);
                if (File.Exists(config))
                {
                    return config;
                }

                if (Directory.Exists(Path.Combine(current, ".git")))
                {
                    return config;
                }

                string parent = Directory.GetParent(current)?.FullName;
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }

            string fallbackRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            return Path.Combine(fallbackRoot, EditorPrefKeys.ProjectConfigFileName);
        }

        private void ConfigureWatcher(string pathToWatch)
        {
            watcher?.Dispose();
            watcher = null;

            string directory = Path.GetDirectoryName(pathToWatch);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            watcher = new FileSystemWatcher(directory, Path.GetFileName(pathToWatch))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Changed += OnConfigFileChanged;
            watcher.Created += OnConfigFileChanged;
            watcher.Renamed += OnConfigFileChanged;
            watcher.EnableRaisingEvents = true;
        }

        private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            if (disposed)
            {
                return;
            }

            EditorApplication.delayCall += () =>
            {
                if (disposed)
                {
                    return;
                }

                LoadOrDefault();
                OnReloaded?.Invoke();
            };
        }
    }
}
