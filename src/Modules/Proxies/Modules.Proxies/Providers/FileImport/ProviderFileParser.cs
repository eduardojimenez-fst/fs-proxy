using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Dtos;

namespace FSH.Modules.Proxies.Providers.FileImport;

public sealed record ProviderFileParseResult(
    IReadOnlyList<ProviderProxyRecord> Records, IReadOnlyList<FileImportRowError> Errors);

/// <summary>
/// Parses the platform's canonical proxy-list CSV format (see the design spec) into
/// <see cref="ProviderProxyRecord"/>s. A pure function of the file's text: no DB/crypto dependency,
/// and blank optional columns stay <c>null</c> here — default-credential/geolocation/kind
/// substitution is the file-import command handler's job (Task 5), not this parser's.
/// </summary>
public static class ProviderFileParser
{
    private static readonly string[] ExpectedHeader =
        ["Host", "Port", "Protocol", "Username", "Password", "Geolocation", "ProxyKind"];

    public static ProviderFileParseResult Parse(string csvContent)
    {
        ArgumentNullException.ThrowIfNull(csvContent);

        var lines = csvContent.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            throw new FormatException("The file is empty.");
        }

        var header = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        if (!header.SequenceEqual(ExpectedHeader, StringComparer.OrdinalIgnoreCase))
        {
            throw new FormatException(
                $"Expected header \"{string.Join(',', ExpectedHeader)}\", got \"{lines[0]}\".");
        }

        var records = new List<ProviderProxyRecord>();
        var errors = new List<FileImportRowError>();

        for (int i = 1; i < lines.Length; i++)
        {
            int lineNumber = i + 1; // 1-based; the header occupies line 1
            var columns = lines[i].Split(',');
            if (columns.Length != ExpectedHeader.Length)
            {
                errors.Add(new FileImportRowError(lineNumber,
                    $"Expected {ExpectedHeader.Length} columns, got {columns.Length}."));
                continue;
            }

            var host = columns[0].Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                errors.Add(new FileImportRowError(lineNumber, "Host is required."));
                continue;
            }

            var portText = columns[1].Trim();
            if (!int.TryParse(portText, out var port) || port is <= 0 or > 65535)
            {
                errors.Add(new FileImportRowError(lineNumber, $"\"{portText}\" is not a valid port."));
                continue;
            }

            var protocolText = columns[2].Trim();
            var protocol = ProxyProtocol.Http;
            if (protocolText.Length > 0 &&
                (!Enum.TryParse(protocolText, ignoreCase: true, out protocol) ||
                 !Enum.IsDefined(protocol)))
            {
                errors.Add(new FileImportRowError(lineNumber,
                    $"\"{protocolText}\" is not a recognized protocol (Http, Https, Socks5)."));
                continue;
            }

            var kindText = columns[6].Trim();
            ProxyKind? kind = null;
            if (kindText.Length > 0)
            {
                if (!Enum.TryParse<ProxyKind>(kindText, ignoreCase: true, out var parsedKind) ||
                    !Enum.IsDefined(parsedKind))
                {
                    errors.Add(new FileImportRowError(lineNumber,
                        $"\"{kindText}\" is not a recognized proxy kind (DataCenter, Residential, Mobile, Dedicated)."));
                    continue;
                }
                kind = parsedKind;
            }

            records.Add(new ProviderProxyRecord(
                ExternalId: $"file:{host}:{port}", Host: host, Port: port, Protocol: protocol,
                Username: NullIfBlank(columns[3]), Password: NullIfBlank(columns[4]), IsActive: true,
                Geolocation: NullIfBlank(columns[5]), ProviderGrouping: null, Kind: kind));
        }

        return new ProviderFileParseResult(records, errors);
    }

    private static string? NullIfBlank(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
