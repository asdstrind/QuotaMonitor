using QuotaMonitor.Core.Models;
namespace QuotaMonitor.Core.Adapters;
public interface IProviderAdapter
{
    string Id { get; }
    Task<ProviderSnapshot> RefreshAsync(bool force, CancellationToken cancellationToken);
}
public interface ISecretStore { string? Read(); void Write(string value); void Delete(); }
