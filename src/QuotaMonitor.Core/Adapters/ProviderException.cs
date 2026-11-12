namespace QuotaMonitor.Core.Adapters;

// Only fixed, redacted messages cross the adapter/UI boundary.
public class ProviderException(string message) : Exception(message);
