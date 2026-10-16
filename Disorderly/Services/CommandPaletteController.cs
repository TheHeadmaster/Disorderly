using Disorderly.Commands;
using Disorderly.Routing;

namespace Disorderly.Services;

public enum PaletteState
{
    Hidden,
    Opening,
    Visible,
    Dismissing,
}

public interface ICommandPaletteView : IDisposable
{
    event EventHandler<PaletteDismissEventArgs>? DismissRequested;
    event EventHandler<PaletteGestureEventArgs>? GestureReceived;
    void Render(PalettePresentation presentation);
    bool ShowAtForegroundMonitor();
    void Hide();
}

/// <summary>Owns visibility and one transient, snapshot-isolated interaction per activation.</summary>
public sealed class CommandPaletteController : IDisposable
{
    private readonly ICommandPaletteView view;
    private readonly CommandConfigurationStore configurations;
    private CommandInteraction? interaction;
    private CommandInvocation? selection;
    private Guid activationId;
    private bool disposed;

    public CommandPaletteController(ICommandPaletteView view, CommandConfigurationStore? configurations = null)
    {
        this.view = view;
        this.configurations = configurations ?? PaletteConfiguration.CreateEmptyStore();
        this.view.DismissRequested += this.OnDismissRequested;
        this.view.GestureReceived += this.OnGestureReceived;
    }

    public PaletteState State { get; private set; } = PaletteState.Hidden;

    public void Toggle()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.State == PaletteState.Visible)
        {
            this.Dismiss();
        }
        else if (this.State == PaletteState.Hidden)
        {
            this.Open();
        }
    }

    public void Open()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.State != PaletteState.Hidden)
        {
            return;
        }

        this.State = PaletteState.Opening;
        try
        {
            this.interaction = this.configurations.CreateInteraction();
            // Also guard programmer-injected stores that omitted the palette reservation validator.
            if (this.interaction.Snapshot.Routing.Contexts.SelectMany(context => context.Bindings)
                .Any(binding => binding.Gesture == PaletteConfiguration.ActivationGesture))
            {
                throw new InvalidOperationException("Palette configurations must reserve Ctrl+Shift+D. Use PaletteConfiguration.CreateStore().");
            }

            this.activationId = Guid.NewGuid();
            this.selection = null;
            this.Render();
            if (this.State != PaletteState.Opening)
            {
                return;
            }

            bool focused = this.view.ShowAtForegroundMonitor();
            if (this.State == PaletteState.Opening)
            {
                this.State = PaletteState.Visible;
                if (!focused)
                {
                    this.Dismiss();
                }
            }
        }
        catch
        {
            this.Dismiss();
            throw;
        }
    }

    public void Dismiss()
    {
        if (this.State is not (PaletteState.Visible or PaletteState.Opening))
        {
            return;
        }

        this.State = PaletteState.Dismissing;
        this.interaction = null;
        this.selection = null;
        this.activationId = Guid.Empty;
        try
        {
            this.view.Hide();
        }
        finally
        {
            this.State = PaletteState.Hidden;
        }
    }

    private void OnGestureReceived(object? sender, PaletteGestureEventArgs args)
    {
        if (this.State != PaletteState.Visible || args.ActivationId != this.activationId || this.interaction is null)
        {
            return;
        }

        RoutingResult result = this.interaction.Routing.Route(args.Gesture);
        switch (result)
        {
            case RoutingResult.DismissRequested:
                this.Dismiss();
                return;
            case RoutingResult.ContextEntered:
            case RoutingResult.ContextReturned:
                this.selection = null;
                this.Render();
                break;
            case RoutingResult.CommandRequested request:
                this.selection = this.interaction.Prepare(request).Invocation;
                this.Render();
                break;
        }
    }

    private void Render()
    {
        CommandInteraction current = this.interaction!;
        RoutingSession routing = current.Routing;
        this.view.Render(new PalettePresentation(this.activationId, routing.ActiveContext.DisplayName,
            string.Join(" / ", routing.NavigationPath.Select(id => current.Snapshot.Routing.GetContext(id).DisplayName)),
            [.. routing.AvailableBindings.Select(binding => binding.Destination switch
            {
                BindingDestination.Context context => new PaletteBindingLabel(binding.Gesture.ToString(), current.Snapshot.Routing.GetContext(context.ContextId).DisplayName, "Context"),
                BindingDestination.Command command => new PaletteBindingLabel(binding.Gesture.ToString(), current.Snapshot.Catalog.Find(command.CommandId)!.DisplayName, "Command"),
                _ => throw new InvalidOperationException("Validated destination required."),
            })], routing.NavigationPath.Length == 1, this.selection));
    }

    private void OnDismissRequested(object? sender, PaletteDismissEventArgs args)
    {
        if (args.ActivationId == this.activationId)
        {
            this.Dismiss();
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.Dismiss();
        this.view.DismissRequested -= this.OnDismissRequested;
        this.view.GestureReceived -= this.OnGestureReceived;
        this.view.Dispose();
        this.disposed = true;
    }
}