using System.Net;
using MaxMind.GeoIP2;
using Microsoft.Extensions.Logging;

namespace Zaagl.Modules.Resolution.Application;

public sealed record GeoLocation(string? Country, string? City);

public sealed class GeoIpOptions
{
    public const string Section = "GeoIp";

    /// <summary>Path to a MaxMind GeoLite2/GeoIP2 City .mmdb file. Optional: Cloudflare's country header is used otherwise.</summary>
    public string? DatabasePath { get; set; }
}

public interface IGeoLookup
{
    GeoLocation Lookup(string? ip);
}

public sealed class NullGeoLookup : IGeoLookup
{
    public GeoLocation Lookup(string? ip) => new(null, null);
}

public sealed class MaxMindGeoLookup : IGeoLookup, IDisposable
{
    private readonly DatabaseReader _reader;
    private readonly ILogger<MaxMindGeoLookup> _logger;

    public MaxMindGeoLookup(string databasePath, ILogger<MaxMindGeoLookup> logger)
    {
        _reader = new DatabaseReader(databasePath);
        _logger = logger;
    }

    public GeoLocation Lookup(string? ip)
    {
        if (!IPAddress.TryParse(ip, out var address)) return new(null, null);
        try
        {
            return _reader.TryCity(address, out var city) && city is not null
                ? new(city.Country.IsoCode, city.City.Name)
                : new(null, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GeoIP lookup failed");
            return new(null, null);
        }
    }

    public void Dispose() => _reader.Dispose();
}
