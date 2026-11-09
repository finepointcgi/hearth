using Microsoft.AspNetCore.Components;

namespace Hearth.UI.State;

/// <summary>Lets handlers written for HTML change events (ChangeEventArgs) serve MudBlazor's ValueChanged.</summary>
public static class Ev
{
    public static Func<T, Task> Change<T>(Func<ChangeEventArgs, Task> handler) => v => handler(new ChangeEventArgs { Value = v });
    public static Action<T> Change<T>(Action<ChangeEventArgs> handler) => v => handler(new ChangeEventArgs { Value = v });
    public static Func<T, Task> Change<T>(Func<Task> handler) => _ => handler();
    public static Action<T> Change<T>(Action handler) => _ => handler();
}
