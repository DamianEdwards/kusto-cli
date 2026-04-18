namespace Kusto.Cli;

public static class ClusterUtilities
{
    // Fixed Kusto hostnames that front ADX as a proxy (ADE / AzureMonitor / Aria / security
    // platform). Unlike classic ADX clusters (e.g., *.kusto.windows.net), a request to these
    // hosts may carry a workspace- or resource-specific path that MUST be preserved end-to-end;
    // collapsing to the bare hostname causes the proxy to reject the request
    // (e.g., InvalidClusterHostName from prod-adxproxy).
    //
    // Source: the AllowedKustoHostnames entries in the public well-known Kusto endpoints list
    // shipped with the official Azure Data Explorer SDKs (MIT). The list below is the union
    // across all sovereign clouds. The azure-kusto-python copy is the superset authoritative
    // reference today:
    //   https://github.com/Azure/azure-kusto-python/blob/master/azure-kusto-data/azure/kusto/data/wellKnownKustoEndpoints.json
    // Mirrored (and kept in sync) by azure-kusto-go:
    //   https://github.com/Azure/azure-kusto-go/blob/master/azkustodata/trusted_endpoints/well_known_kusto_endpoints.json
    private static readonly HashSet<string> ProxyHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        // Public cloud (login.microsoftonline.com)
        "ade.applicationinsights.io",
        "ade.loganalytics.io",
        "adx.aimon.applicationinsights.azure.com",
        "adx.applicationinsights.azure.com",
        "adx.int.applicationinsights.azure.com",
        "adx.int.loganalytics.azure.com",
        "adx.int.monitor.azure.com",
        "adx.loganalytics.azure.com",
        "adx.monitor.azure.com",
        "kusto.aria.microsoft.com",
        "eu.kusto.aria.microsoft.com",
        "api.securityplatform.microsoft.com",

        // US Government (Fairfax)
        "adx.applicationinsights.azure.us",
        "adx.loganalytics.azure.us",
        "adx.monitor.azure.us",

        // China (Mooncake)
        "adx.applicationinsights.azure.cn",
        "adx.loganalytics.azure.cn",
        "adx.monitor.azure.cn",

        // US Nat (EagleX)
        "adx.applicationinsights.azure.eaglex.ic.gov",
        "adx.loganalytics.azure.eaglex.ic.gov",
        "adx.monitor.azure.eaglex.ic.gov",

        // US Sec (scloud)
        "adx.applicationinsights.azure.microsoft.scloud",
        "adx.loganalytics.azure.microsoft.scloud",
        "adx.monitor.azure.microsoft.scloud",

        // France sovereign (Bleu)
        "adx.applicationinsights.azure.fr",
        "adx.loganalytics.azure.fr",
        "adx.monitor.azure.fr",

        // Germany sovereign (Delos)
        "adx.applicationinsights.azure.de",
        "adx.loganalytics.azure.de",
        "adx.monitor.azure.de",

        // Singapore sovereign (GovSG)
        "adx.applicationinsights.azure.sg",
        "adx.loganalytics.azure.sg",
        "adx.monitor.azure.sg",
    };

    public static string NormalizeClusterUrl(string clusterUrl)
    {
        if (!Uri.TryCreate(clusterUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new UserFacingException($"'{clusterUrl}' is not a valid cluster URL.");
        }

        if (IsProxyHost(uri.Host) && uri.AbsolutePath.Length > 1)
        {
            var builder = new UriBuilder(uri)
            {
                Query = string.Empty,
                Fragment = string.Empty,
            };

            return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static bool IsProxyHost(string host) => ProxyHosts.Contains(host);

    public static KnownCluster? FindKnownCluster(KustoConfig config, string clusterReference)
    {
        foreach (var cluster in config.Clusters)
        {
            if (string.Equals(cluster.Name, clusterReference, StringComparison.OrdinalIgnoreCase))
            {
                return cluster;
            }
        }

        if (Uri.TryCreate(clusterReference, UriKind.Absolute, out _))
        {
            var normalizedReference = NormalizeClusterUrl(clusterReference);
            foreach (var cluster in config.Clusters)
            {
                if (string.Equals(NormalizeClusterUrl(cluster.Url), normalizedReference, StringComparison.OrdinalIgnoreCase))
                {
                    return cluster;
                }
            }
        }

        return null;
    }

    public static KustoConfig NormalizeConfig(KustoConfig? config)
    {
        if (config is null)
        {
            return new KustoConfig();
        }

        config.Clusters ??= [];
        config.SchemaCache ??= new SchemaCacheConfig();
        config.SchemaCache.Overrides ??= [];
        config.SchemaCache.Path = string.IsNullOrWhiteSpace(config.SchemaCache.Path)
            ? null
            : config.SchemaCache.Path.Trim();

        var normalizedDatabases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config.DefaultDatabases is not null)
        {
            foreach (var pair in config.DefaultDatabases)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                normalizedDatabases[NormalizeClusterUrl(pair.Key)] = pair.Value;
            }
        }

        config.DefaultDatabases = normalizedDatabases;
        if (!string.IsNullOrWhiteSpace(config.DefaultClusterUrl))
        {
            config.DefaultClusterUrl = NormalizeClusterUrl(config.DefaultClusterUrl);
        }

        for (var i = 0; i < config.Clusters.Count; i++)
        {
            var current = config.Clusters[i];
            current.Name = current.Name.Trim();
            current.Url = NormalizeClusterUrl(current.Url);
        }

        var normalizedOverrides = new List<SchemaCacheOverride>();
        foreach (var current in config.SchemaCache.Overrides)
        {
            if (string.IsNullOrWhiteSpace(current.ClusterUrl) ||
                string.IsNullOrWhiteSpace(current.Database))
            {
                continue;
            }

            normalizedOverrides.Add(new SchemaCacheOverride
            {
                ClusterUrl = NormalizeClusterUrl(current.ClusterUrl),
                Database = current.Database.Trim(),
                TtlSeconds = current.TtlSeconds
            });
        }

        config.SchemaCache.Overrides = normalizedOverrides;

        return config;
    }
}
