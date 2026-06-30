using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using redb.Tsak.Client;
using redb.Tsak.Contracts;
using redb.Tsak.Web.Pro;

namespace redb.Tsak.Web.Services;

/// <summary>
/// Standalone implementation of <see cref="INodeClientProvider"/>.
/// Reads node URLs from config (<c>Tsak:Web:Nodes[]</c> or <c>Tsak:Web:StandaloneUrl</c> fallback).
/// No EAV, no database — pure config-driven.
/// </summary>
public sealed class StandaloneClientProvider : INodeClientProvider
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<StandaloneClientProvider> _logger;
    private readonly ConcurrentDictionary<string, TsakApiClient> _clients = new();
    private ClusterTopology? _topology;

    public StandaloneClientProvider(
        IConfiguration configuration,
        ILogger<StandaloneClientProvider> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsCluster => false;

    /// <inheritdoc />
    public ClusterTopology? Topology => _topology;

    /// <inheritdoc />
    public Task<ClusterTopology> LoadTopologyAsync(bool forceRefresh = false)
    {
        if (_topology is not null)
        {
            // Refresh heartbeat so IsAlive stays true for standalone nodes (no real heartbeat mechanism)
            var now = DateTimeOffset.UtcNow;
            foreach (var node in _topology.Nodes)
                node.LastHeartbeat = now;
            return Task.FromResult(_topology);
        }

        var nodes = new List<NodeInfo>();

        // Try Tsak:Web:Nodes[] array first
        var nodesSection = _configuration.GetSection("Tsak:Web:Nodes");
        if (nodesSection.Exists())
        {
            var children = nodesSection.GetChildren().ToList();
            foreach (var child in children)
            {
                var id = child["Id"] ?? $"node-{children.IndexOf(child) + 1}";
                var url = child["Url"];
                if (string.IsNullOrEmpty(url)) continue;

                nodes.Add(new NodeInfo
                {
                    NodeId = id,
                    Hostname = id,
                    ApiEndpoint = url,
                    Status = NodeStatus.Online,
                    LastHeartbeat = DateTimeOffset.UtcNow,
                    StartedAt = DateTimeOffset.UtcNow
                });
            }
        }

        // Fallback: Tsak:Web:StandaloneUrl (single node, backward compatibility)
        if (nodes.Count == 0)
        {
            var standaloneUrl = _configuration["Tsak:Web:StandaloneUrl"] ?? "http://localhost:9090";
            nodes.Add(new NodeInfo
            {
                NodeId = "default",
                Hostname = "localhost",
                ApiEndpoint = standaloneUrl,
                Status = NodeStatus.Online,
                LastHeartbeat = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow
            });
        }

        _topology = new ClusterTopology
        {
            ClusterName = "standalone",
            Nodes = nodes,
            Groups = [],
            Assignments = [],
            Leader = null
        };

        _logger.LogDebug("Standalone topology loaded: {NodeCount} node(s)", nodes.Count);
        return Task.FromResult(_topology);
    }

    /// <inheritdoc />
    public TsakApiClient? GetClient(string nodeId)
    {
        if (_clients.TryGetValue(nodeId, out var client))
            return client;

        var node = _topology?.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (node is null || string.IsNullOrEmpty(node.ApiEndpoint))
        {
            _logger.LogWarning("GetClient({NodeId}): node not found in standalone config", nodeId);
            return null;
        }

        // Check for per-node ApiKey in config
        var nodesSection = _configuration.GetSection("Tsak:Web:Nodes");
        string? apiKey = null;
        if (nodesSection.Exists())
        {
            foreach (var child in nodesSection.GetChildren())
            {
                if (child["Id"] == nodeId || child["Url"] == node.ApiEndpoint)
                {
                    apiKey = child["ApiKey"];
                    break;
                }
            }
        }

        client = new TsakApiClient(node.ApiEndpoint, apiKey);
        _clients[nodeId] = client;
        return client;
    }

    /// <inheritdoc />
    public Task<bool> RemoveNodeAsync(string nodeId) => Task.FromResult(false);

    public void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }
}
