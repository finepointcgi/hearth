using Microsoft.AspNetCore.Components;

namespace Hearth.UI.State;

/// <summary>Re-renders whenever <see cref="AppState"/> changes (models loaded, chats list updated…).</summary>
public abstract class StatefulComponent : ComponentBase, IDisposable
{
    [Inject] protected AppState State { get; set; } = default!;

    protected override void OnInitialized()
    {
        State.Changed += OnStateChanged;
    }

    private void OnStateChanged() => InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        State.Changed -= OnStateChanged;
        GC.SuppressFinalize(this);
    }
}
