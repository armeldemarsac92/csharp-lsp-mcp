using System.Text;
using CSharpLspMcp.Lsp;

namespace CSharpLspMcp.Analysis.Lsp;

public sealed class CSharpSearchAnalysisService
{
    private readonly LspClient _lspClient;

    public CSharpSearchAnalysisService(LspClient lspClient)
    {
        _lspClient = lspClient;
    }

    public async Task<string> SearchSymbolsAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var symbols = await _lspClient.SearchWorkspaceSymbolsAsync(query, cancellationToken);
        if (symbols == null || symbols.Length == 0)
            return "No workspace symbols found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {symbols.Length} workspace symbol(s):\n");

        foreach (var symbol in symbols.Take(maxResults))
        {
            sb.AppendLine($"• {symbol.Name} ({symbol.Kind})");
            if (!string.IsNullOrEmpty(symbol.ContainerName))
                sb.AppendLine($"  Container: {symbol.ContainerName}");

            var path = new Uri(symbol.Location.Uri).LocalPath;
            sb.AppendLine($"  {path}:{symbol.Location.Range.Start.Line + 1}");
        }

        if (symbols.Length > maxResults)
            sb.AppendLine($"\n... and {symbols.Length - maxResults} more");

        return sb.ToString();
    }
}
