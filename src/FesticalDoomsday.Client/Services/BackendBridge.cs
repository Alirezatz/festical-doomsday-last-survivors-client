using System.Text.Json;
using FesticalDoomsday.Client.Backend;

namespace FesticalDoomsday.Client.Services;

/// <summary>
/// The one shared backend that serves every account. Commands and events are
/// routed by an "account" id, so the window never starts a separate worker
/// process per account.
/// </summary>
public sealed class BackendBridge : IDisposable
{
    private readonly BackendService _service = new();
    private readonly Action<JsonElement> _onEvent;
    private bool _disposed;

    public BackendBridge(Action<JsonElement> onEvent)
    {
        _onEvent = onEvent;
        _service.Output += HandleOutput;
    }

    public bool IsRunning => !_disposed && _service.IsRunning;

    public void Start() => _service.Start();

    public void Send(object command)
    {
        if (_disposed)
            throw new InvalidOperationException("The shared backend is not running.");
        _service.Send(JsonSerializer.Serialize(command));
    }

    private void HandleOutput(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            _onEvent(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                type = "log",
                message = line
            }));
            _onEvent(document.RootElement.Clone());
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _service.Output -= HandleOutput;
        _service.Dispose();
    }
}
